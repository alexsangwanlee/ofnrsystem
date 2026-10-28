using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Mail;
using Ofnr.Server.Mcp;
using Ofnr.Server.Realtime;
using static Ofnr.Server.Projects.ProjectEndpoints;

namespace Ofnr.Server.Ops;

/// <summary>Health and the engine check, the Today and Agents pages, and settings.</summary>
public static class OpsEndpoints
{
    const string CheckMessage = "엔진 점검 완료";
    const string CheckTool = "mcp__ofnr__report_progress";

    /// <summary>The check job can wait behind running jobs; past this the CEO gets an answer instead of a button that never returns. Settable for tests.</summary>
    public static TimeSpan CheckTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public static void MapOpsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", (Health health, CancellationToken ct) => health.BuildAsync(ct));

        // A real one-turn job: it catches a missing login and an engine that cannot call the dashboard's MCP tools (openai/codex#24135).
        app.MapPost("/api/health/engine-check/{engine}", async (OfnrDb db, RecipeCatalog recipes, IJobQueue queue, IOpsNotifier ops, Health health, string engine, CancellationToken ct) =>
        {
            if (engine is not ("claude" or "codex")) return Error(404, "알 수 없는 엔진입니다.");
            var job = new Job
            {
                RecipeId = "free", Engine = engine, Title = $"엔진 점검 · {(engine == "claude" ? "Claude Code" : "Codex")}",
                Instruction = $"엔진 점검입니다. 파일을 만들지 말고 대시보드 MCP 서버 ofnr의 report_progress 도구({CheckTool})를 message 값 \"{CheckMessage}\"로 한 번 호출한 뒤 끝내세요.",
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            await JobEndpoints.QueuedAsync(db, recipes, queue, ops, job.Id);

            var until = DateTimeOffset.UtcNow + CheckTimeout;
            JobStatus status;
            while ((status = await db.Jobs.Where(j => j.Id == job.Id).Select(j => j.Status).SingleAsync(ct)) is JobStatus.Queued or JobStatus.Running)
            {
                if (DateTimeOffset.UtcNow > until)
                {
                    await queue.CancelAsync(job.Id);
                    return Results.Ok(await health.RecordCheckAsync(engine, false, $"엔진 점검이 {CheckTimeout.TotalMinutes:0}분 안에 끝나지 않았습니다."));
                }
                await Task.Delay(500, ct);
            }
            if (status != JobStatus.Review)
            {
                var error = await db.JobRuns.Where(r => r.JobId == job.Id).OrderByDescending(r => r.Seq).Select(r => r.Error).FirstOrDefaultAsync(ct);
                return Results.Ok(await health.RecordCheckAsync(engine, false, $"엔진 점검이 실패했습니다: {error ?? "작업 기록을 확인해 주세요."}"));
            }
            // A finished check is no result to review.
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Done).SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
            await ops.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, job.Id));
            // The engine called the tool and the call reached this server: its own text can echo the line, and Codex can start a call it then cancels.
            var reported = await db.JobEvents.AnyAsync(e => e.JobId == job.Id && e.Kind == "tool" && e.Summary.StartsWith(CheckTool), ct)
                && await db.JobEvents.AnyAsync(e => e.JobId == job.Id && e.Raw == OfnrTools.ReportProgressRaw && e.Summary == CheckMessage, ct);
            return Results.Ok(reported
                ? await health.RecordCheckAsync(engine, true, "엔진 점검을 통과했습니다.")
                : await health.RecordCheckAsync(engine, false, "엔진이 대시보드 MCP 도구를 쓰지 못했습니다. 엔진의 MCP 설정과 승인 설정을 확인해 주세요."));
        });

        app.MapGet("/api/today", async (OfnrDb db, RecipeCatalog recipes) =>
        {
            var questions = await db.Approvals.Where(a => a.Status == ApprovalStatus.Pending).OrderBy(a => a.CreatedAt)
                .Select(a => new AttentionDto("approval", a.JobId, a.Title, a.Detail, a.Id, a.Kind)).ToListAsync();
            // A job held by an open question shows once, as the question.
            var jobs = await db.Jobs
                .Where(j => j.Status == JobStatus.Review || j.Status == JobStatus.Failed
                    || j.Status == JobStatus.NeedsAttention && !db.Approvals.Any(a => a.JobId == j.Id && a.Status == ApprovalStatus.Pending))
                .OrderByDescending(j => j.UpdatedAt)
                // Held before its engine started (an app to open, AI files to delete; a restart keeps the reason), or a run a restart stopped.
                .Select(j => new AttentionDto(
                    j.Status == JobStatus.Review ? "review" : j.Status == JobStatus.Failed ? "failed"
                    : db.JobRuns.Where(r => r.JobId == j.Id).OrderByDescending(r => r.Seq).Select(r => r.Status == RunStatus.Blocked || r.Error != null).FirstOrDefault()
                        ? "blocked" : "stopped",
                    j.Id, j.Title,
                    db.JobRuns.Where(r => r.JobId == j.Id).OrderByDescending(r => r.Seq).Select(r => r.Error).FirstOrDefault() ?? (j.Result == null ? null : j.Result.Summary),
                    null, null))
                .ToListAsync();
            var unsorted = await db.MailMessages.Where(m => m.ProjectId == null && (m.Status == MailStatus.New || m.Status == MailStatus.NeedsReply))
                .OrderByDescending(m => m.ReceivedAt).Take(20).ToSummaries().ToListAsync();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var upcoming = (await db.Projects.Where(p => p.Status == ProjectStatus.Active)
                    .Select(p => new { p.Id, p.JobNo, p.Name, p.InstallDate, p.OpenDate, p.EndDate, p.TeardownDate }).ToListAsync())
                .SelectMany(p => new[] { ("install", p.InstallDate), ("open", p.OpenDate), ("end", p.EndDate), ("teardown", p.TeardownDate) }
                    .Where(d => d.Item2 >= today && d.Item2 <= today.AddDays(14))
                    .Select(d => new UpcomingDto(p.Id, p.JobNo, p.Name, d.Item1, d.Item2!.Value)))
                .OrderBy(u => u.Date).ToList();
            var running = await db.Jobs.Where(j => j.Status == JobStatus.Running).OrderBy(j => j.UpdatedAt).ToDtosAsync(db, recipes);
            List<AttentionDto> attention = [.. questions, .. jobs];
            return new TodayDto(new(attention.Count, running.Count, upcoming.Count(u => u.Date < today.AddDays(7))), attention, unsorted, upcoming, running);
        });

        app.MapGet("/api/agents", async (OfnrDb db, RecipeCatalog recipes, IJobQueue queue, Health health) =>
        {
            var now = DateTimeOffset.Now;
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).ToUniversalTime();
            var byRecipe = (await db.JobRuns.Where(r => r.StartedAt >= monthStart).GroupBy(r => r.Job.RecipeId)
                    .Select(g => new { g.Key, Usd = g.Sum(r => r.CostUsd) ?? 0, Runs = g.Count() }).ToListAsync())
                .Select(g => new RecipeCostDto(g.Key, g.Usd, g.Runs)).OrderByDescending(r => r.Usd).ThenBy(r => r.RecipeId).ToList();
            return new AgentsDto(
                await db.Jobs.Where(j => j.Status == JobStatus.Running).OrderBy(j => j.UpdatedAt).ToDtosAsync(db, recipes),
                await db.Jobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.UpdatedAt).ToDtosAsync(db, recipes),
                queue.Locks, new(byRecipe.Sum(r => r.Usd), byRecipe), await health.EnginesAsync(fresh: false));
        });

        app.MapGet("/api/settings", (SettingsStore settings) => SettingsDto.From(settings.Current));

        app.MapPut("/api/settings", async (SettingsStore settings, OfnrOptions input) =>
        {
            if (Invalid(input) is { } message) return Error(400, message);
            input.ConnectionString = settings.Current.ConnectionString; // never set through the API
            await settings.SaveAsync(input);
            return Results.Ok(SettingsDto.From(settings.Current));
        });

        app.MapPut("/api/settings/mail-password", (SecretStore secrets, PasswordInput input) =>
        {
            if (string.IsNullOrEmpty(input.Password)) return Error(400, "비밀번호를 입력해 주세요.");
            try { secrets.Save("mail", input.Password); }
            catch (PlatformNotSupportedException e) { return Error(400, e.Message); }
            return Results.NoContent();
        });

        app.MapPost("/api/settings/mail-test", async (SettingsStore settings, IImapSource imap) =>
        {
            if (settings.Current.Mail.Address is not { Length: > 0 } address) return new CheckDto("메일", false, MailSync.NotConfigured);
            try
            {
                await imap.OpenAsync("INBOX");
                return new CheckDto("메일", true, $"{address} 받은메일함을 열었습니다.");
            }
            catch (Exception e) // any failure is the answer to this test
            {
                return new CheckDto("메일", false, e is MailException ? e.Message : $"메일 서버에 연결하지 못했습니다: {e.Message}");
            }
        });
    }

    // Values the server cannot start or run with; the screen checks the same and more before saving.
    static string? Invalid(OfnrOptions o) =>
        o.Mail is null || new[] { o.ClaudeExe, o.CodexExe, o.PgDumpPath }.Any(string.IsNullOrWhiteSpace) ? "요청 형식이 올바르지 않습니다."
        : o.DefaultEngine is not ("claude" or "codex") ? "기본 엔진은 Claude Code 또는 Codex로 골라 주세요."
        : o.MaxConcurrentJobs is < 1 or > 8 ? "동시 실행 수는 1에서 8 사이로 입력해 주세요."
        : o.MailSyncMinutes is < 1 or > 1440 ? "동기화 간격은 1분에서 1440분 사이로 입력해 주세요."
        // Port 0 lets the system pick, as the desktop app's sidecar does.
        : o.Port is < 0 or > 65535 || new[] { o.Mail.ImapPort, o.Mail.SmtpPort }.Any(p => p is < 1 or > 65535) ? "포트는 1에서 65535 사이로 입력해 주세요."
        : !$"{o.ProjectsRoot}{o.JobsRoot}".All(char.IsAscii) ? "프로젝트 폴더와 작업 실행 폴더는 영문 경로만 쓸 수 있습니다."
        : null;
}
