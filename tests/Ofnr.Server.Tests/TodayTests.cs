using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;

namespace Ofnr.Server.Tests;

public sealed class TodayTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static MailMessage Mail(long uid, Project? project, MailStatus status) => new()
    {
        Folder = "INBOX", UidValidity = 1, Uid = uid, ThreadKey = $"<m{uid}@tnf.co.kr>", FromAddress = "kim@tnf.co.kr", Subject = $"견적 요청 {uid}",
        ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(uid), Project = project, Status = status,
    };

    [Fact]
    public async Task Today_lists_pending_approval_and_upcoming_dates()
    {
        await using var host = new TestHost();
        var today = DateOnly.FromDateTime(DateTime.Now);
        Project project;
        Job asking, review;
        ApprovalDto question;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
            project = new Project
            {
                JobNo = "OFNR-26001", Name = "성수 팝업", Slug = "seongsu", FolderPath = "/p/1",
                InstallDate = today.AddDays(2), OpenDate = today.AddDays(9), EndDate = today.AddDays(20), TeardownDate = today.AddDays(-1),
            };
            var lost = new Project { JobNo = "OFNR-26002", Name = "무산된 행사", Slug = "lost", FolderPath = "/p/2", Status = ProjectStatus.Lost, OpenDate = today };
            asking = new Job { Project = project, RecipeId = "quote", Title = "견적서 · 성수 팝업", Status = JobStatus.NeedsAttention };
            review = new Job { RecipeId = "free", Title = "자유 지시", Status = JobStatus.Review, Result = new() { Summary = "초안을 만들었습니다" } };
            db.AddRange(project, lost, asking, review, new Job { RecipeId = "free", Title = "실행 중", Status = JobStatus.Running });
            db.MailMessages.AddRange(Mail(1, null, MailStatus.New), Mail(2, project, MailStatus.Linked), Mail(3, null, MailStatus.Done), Mail(4, null, MailStatus.NeedsReply));
            await db.SaveChangesAsync(Ct);
            question = await new Approvals(db, new RecordingNotifier()).CreateQuestionAsync(asking.Id, null, "부스 크기를 3x3으로 할까요?", ["3x3", "6x3"]);
        }

        var t = await host.Authed().GetFromJsonAsync<JsonElement>("/api/today", Ct);

        // The job waiting on its question shows once, as the question.
        var attention = t.GetProperty("attention").EnumerateArray().ToList();
        Assert.Equal(2, attention.Count);
        var a = Assert.Single(attention, x => x.GetProperty("kind").GetString() == "approval");
        Assert.Equal((asking.Id, question.Id, "question", "부스 크기를 3x3으로 할까요?"),
            (a.GetProperty("jobId").GetGuid(), a.GetProperty("approvalId").GetGuid(), a.GetProperty("approvalKind").GetString(), a.GetProperty("title").GetString()));
        var r = Assert.Single(attention, x => x.GetProperty("kind").GetString() == "review");
        Assert.Equal((review.Id, "초안을 만들었습니다"), (r.GetProperty("jobId").GetGuid(), r.GetProperty("detail").GetString()));

        var upcoming = t.GetProperty("upcoming").EnumerateArray()
            .Select(u => (u.GetProperty("projectId").GetGuid(), u.GetProperty("jobNo").GetString(), u.GetProperty("kind").GetString(), u.GetProperty("date").GetString()));
        Assert.Equal([(project.Id, "OFNR-26001", "install", $"{today.AddDays(2):yyyy-MM-dd}"), (project.Id, "OFNR-26001", "open", $"{today.AddDays(9):yyyy-MM-dd}")], upcoming);

        Assert.Equal(["견적 요청 4", "견적 요청 1"], t.GetProperty("unsortedMail").EnumerateArray().Select(m => m.GetProperty("subject").GetString()));
        Assert.Equal(["실행 중"], t.GetProperty("running").EnumerateArray().Select(j => j.GetProperty("title").GetString()));
        var counts = t.GetProperty("counts");
        Assert.Equal((2, 1, 1), (counts.GetProperty("attention").GetInt32(), counts.GetProperty("running").GetInt32(), counts.GetProperty("thisWeek").GetInt32()));
    }

    [Fact]
    public async Task A_job_stopped_by_a_restart_is_not_waiting_for_an_app()
    {
        await using var host = new TestHost();
        Job stopped, blocked;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
            stopped = new Job { RecipeId = "free", Title = "멈춘 작업", Status = JobStatus.NeedsAttention };
            blocked = new Job { RecipeId = "model3d", Title = "3D 모델", Status = JobStatus.NeedsAttention };
            db.AddRange(stopped, blocked,
                new JobRun { Job = stopped, Seq = 1, Status = RunStatus.Stopped },
                new JobRun { Job = blocked, Seq = 1, Status = RunStatus.Blocked, Error = "SketchUp을 열고 MCP Server를 Start 해 주세요." });
            await db.SaveChangesAsync(Ct);
        }

        var t = await host.Authed().GetFromJsonAsync<JsonElement>("/api/today", Ct);

        Assert.Equal([(blocked.Id, "blocked"), (stopped.Id, "stopped")], t.GetProperty("attention").EnumerateArray()
            .Select(a => (a.GetProperty("jobId").GetGuid(), a.GetProperty("kind").GetString())).OrderBy(a => a.Item2));
    }

    [Fact]
    public async Task Agents_sums_this_month_costs_by_recipe()
    {
        await using var h = new JobHost();
        h.Agents.Hold = true;
        var ids = new[] { await h.Job("free"), await h.Job("free"), await h.Job("free") };
        await JobHost.WaitFor(() => h.Agents.Active.Count == 2);
        await using (var db = h.Db())
        {
            var now = DateTimeOffset.UtcNow;
            var quote = new Job { RecipeId = "quote", Title = "견적서", Status = JobStatus.Done };
            db.AddRange(quote,
                new JobRun { Job = quote, Seq = 1, StartedAt = now, CostUsd = 0.5m },
                new JobRun { Job = quote, Seq = 2, StartedAt = now, CostUsd = 0.25m },
                new JobRun { Job = quote, Seq = 3, StartedAt = now.AddMonths(-2), CostUsd = 9m });
            await db.SaveChangesAsync(Ct);
        }

        var a = await h.Send(HttpMethod.Get, "/api/agents");
        h.Agents.ReleaseAll();

        var costs = a.GetProperty("costs");
        Assert.Equal(0.75m, costs.GetProperty("monthUsd").GetDecimal());
        Assert.Equal([("quote", 0.75m, 2), ("free", 0m, 2)], costs.GetProperty("byRecipe").EnumerateArray()
            .Select(r => (r.GetProperty("recipeId").GetString(), r.GetProperty("usd").GetDecimal(), r.GetProperty("runs").GetInt32())));
        Assert.Equal(ids[..2].Order(), a.GetProperty("running").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).Order());
        Assert.Equal([ids[2]], a.GetProperty("queued").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()));
        Assert.Equal(["office", "sketchup", "illustrator", "photoshop"], a.GetProperty("locks").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(["claude", "codex"], a.GetProperty("engines").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }
}
