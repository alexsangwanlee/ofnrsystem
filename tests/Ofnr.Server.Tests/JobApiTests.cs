using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ofnr.Server.Data;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class JobApiTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disallowed_engine_is_400()
    {
        await using var h = new JobHost();
        var p = await h.Project();

        async Task<string?> Rejected(object body) =>
            (await h.Send(HttpMethod.Post, "/api/jobs", body, HttpStatusCode.BadRequest)).GetProperty("error").GetString();

        Assert.Equal("이 작업은 codex로 실행할 수 없습니다.", await Rejected(new { recipeId = "quote", projectId = p.Id, instruction = "x", engine = "codex" }));
        Assert.Equal("스킬 학습은 검토 의견에서만 시작할 수 있습니다.", await Rejected(new { recipeId = "skill-learning", instruction = "x" }));
        Assert.Equal("알 수 없는 작업 종류입니다.", await Rejected(new { recipeId = "nope", instruction = "x" }));
        Assert.Equal("프로젝트를 선택해 주세요.", await Rejected(new { recipeId = "brief", instruction = "x" }));
        await h.Send(HttpMethod.Post, "/api/jobs", new { recipeId = "brief", projectId = Guid.NewGuid(), instruction = "x" }, HttpStatusCode.NotFound);

        var job = await h.Send(HttpMethod.Post, "/api/jobs", new { recipeId = "quote", projectId = p.Id, instruction = "견적서" });
        Assert.Equal(("견적서 · 성수 팝업", "claude", p.JobNo), (job.GetProperty("title").GetString(), job.GetProperty("engine").GetString(), job.GetProperty("jobNo").GetString()));
        var recipes = await h.Send(HttpMethod.Get, "/api/recipes");
        Assert.Equal(["quote"], recipes.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "quote").GetProperty("skills").EnumerateArray().Select(s => s.GetString()));
        var list = await h.Send(HttpMethod.Get, $"/api/jobs?projectId={p.Id}");
        Assert.Equal("견적서", Assert.Single(list.EnumerateArray()).GetProperty("recipeName").GetString());
    }

    [Fact]
    public async Task Feedback_with_skill_creates_learning_job()
    {
        await using var h = new JobHost();
        var p = await h.Project();
        var source = await h.Job("brief", p.Id);
        await h.WaitForStatus(source, JobStatus.Review);
        var skillFile = Path.Combine(h.DataRoot, "claude-plugin", "skills", "quote", "SKILL.md");
        h.Agents.Script = (call, ct) =>
        {
            File.AppendAllText(skillFile, "- 단가는 부가세 별도로 적는다 (2026-10-27, 작업번호)\n");
            return FakeAgents.Default(call, ct);
        };

        var f = await h.Send(HttpMethod.Post, $"/api/jobs/{source}/feedback", new { text = "단가는 부가세 별도로 적어 주세요", skillName = "quote" });

        Assert.Equal("quote", f.GetProperty("skillName").GetString());
        var learningId = f.GetProperty("learningJobId").GetGuid();
        var learning = await h.WaitForStatus(learningId, JobStatus.Done);
        Assert.Equal(("skill-learning", "스킬 학습 · quote", null), (learning.RecipeId, learning.Title, learning.ProjectId));
        foreach (var part in new[] { "quote", skillFile, "단가는 부가세 별도로 적어 주세요", p.JobNo, DateTimeOffset.Now.ToString("yyyy-MM-dd") })
            Assert.Contains(part, learning.Instruction);
        Assert.Contains(skillFile, Assert.Single(h.Agents.Calls, c => c.JobId == learningId).Prompt);
        if (Git(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(skillFile)))!, "log", "-1", "--format=%s") is { } subject)
            Assert.Equal($"skill(quote): 학습 {p.JobNo}", subject);

        var plain = await h.Send(HttpMethod.Post, $"/api/jobs/{source}/feedback", new { text = "좋습니다" });
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("learningJobId").ValueKind);
        var bad = await h.Send(HttpMethod.Post, $"/api/jobs/{source}/feedback", new { text = "x", skillName = "../quote" }, HttpStatusCode.BadRequest);
        Assert.Equal("알 수 없는 스킬입니다.", bad.GetProperty("error").GetString());
        await h.Send(HttpMethod.Post, $"/api/jobs/{source}/feedback", new { text = " " }, HttpStatusCode.BadRequest);
        var detail = await h.Send(HttpMethod.Get, $"/api/jobs/{source}");
        Assert.Equal(["단가는 부가세 별도로 적어 주세요", "좋습니다"], detail.GetProperty("feedback").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
    }

    /// <summary>git's output, or null when git is not installed.</summary>
    static string? Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    [Fact]
    public async Task Complete_sets_done_and_approves_the_jobs_drafts()
    {
        await using var h = new JobHost();
        var p = await h.Project();
        h.Agents.Script = (call, ct) =>
        {
            File.WriteAllText(Path.Combine(p.Folder, "01_PLAN", $"{p.JobNo}_BRIEF_v01.md"), "# 요구사항");
            return FakeAgents.Default(call, ct);
        };
        var id = await h.Job("brief", p.Id);
        await h.WaitForStatus(id, JobStatus.Review);

        var done = await h.Send(HttpMethod.Post, $"/api/jobs/{id}/complete");

        Assert.Equal("Done", done.GetProperty("status").GetString());
        await using var db = h.Db();
        Assert.Equal(ArtifactStatus.InternalApproved, (await db.Artifacts.SingleAsync(a => a.JobId == id, Ct)).Status);
    }

    [Fact]
    public async Task Server_keeps_running_when_the_database_is_unreachable()
    {
        await using var host = new TestHost();
        var app = host.WithWebHostBuilder(b => b.UseSetting("OFNR_DB", "Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=2"));
        var client = app.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:47821") });
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);

        await Task.Delay(500, Ct); // the queue's first pass has failed by now

        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health", Ct)).StatusCode);
    }
}
