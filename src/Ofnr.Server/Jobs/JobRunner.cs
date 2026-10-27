using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Ofnr.Server.Agents;
using Ofnr.Server.Data;
using Ofnr.Server.Mcp;
using Ofnr.Server.Projects;
using Ofnr.Server.Realtime;
using Ofnr.Server.Skills;

namespace Ofnr.Server.Jobs;

/// <summary>Runs one job run on its engine and records everything it does. <see cref="JobQueue"/> decides when.</summary>
public sealed class JobRunner(IServiceScopeFactory scopes, SettingsStore settings, RecipeCatalog recipes, AgentFactory agents, JobTokens tokens,
    IOpsNotifier notifier, IHostApplicationLifetime lifetime, SkillStore skills)
{
    static readonly JsonSerializerOptions ResultJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // The argument that says the most about a tool call, in the order Claude's and Codex's tools name it.
    static readonly string[] ArgKeys = ["command", "file_path", "path", "pattern", "url", "query"];

    public async Task RunAsync(Guid jobId, string instruction, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var o = settings.Current;
        var job = await db.Jobs.Include(j => j.Project).ThenInclude(p => p!.Client).Include(j => j.Mail).SingleAsync(j => j.Id == jobId, CancellationToken.None);
        var run = await NextRunAsync(db, jobId);
        run.Status = RunStatus.Working;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.Error = null;
        job.Status = JobStatus.Running;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));

        Task Log(string kind, string summary) => JobEventLog.AppendAsync(db, notifier, jobId, run.Id, kind, summary);
        // Follow-ups replace Job.Instruction, so the log keeps what each run was asked.
        if (instruction.Length > 0) await Log("system", instruction);

        var recipe = recipes.Get(job.RecipeId);
        var token = tokens.Issue(jobId);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var folder = job.Project?.FolderPath;
        IReadOnlyDictionary<string, (long size, DateTime mtimeUtc)>? before = null;
        AIAgent? agent = null;
        AgentSession? session = null;
        var codexHeld = false;

        // Kept as soon as the engine names its session, so a follow-up continues it after a failure, a cancel, a crash or a restart.
        async Task<bool> KeepSessionAsync()
        {
            if (agent is null || session is null || !session.StateBag.TryGetValue<string>(CliAgent.SessionKey, out var sid) || sid is null || sid == run.SessionId) return false;
            run.SessionId = sid;
            job.EngineSession = (await agent.SerializeSessionAsync(session)).GetRawText();
            return true;
        }

        async Task EndAsync(RunStatus runStatus, JobStatus jobStatus, string? error)
        {
            await KeepSessionAsync();
            (run.Status, run.Error, run.EndedAt) = (runStatus, error, DateTimeOffset.UtcNow);
            (job.Status, job.UpdatedAt) = (jobStatus, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));
        }

        try
        {
            if (recipe is null) throw new InvalidOperationException($"작업 종류를 찾을 수 없습니다: {job.RecipeId}");
            // Mail triage may be running Codex; waiting for it is not part of the time limit.
            if (job.Engine == "codex") codexHeld = await agents.CodexSlot.WaitAsync(Timeout.Infinite, ct);
            limit.CancelAfter(TimeSpan.FromMinutes(recipe.TimeoutMinutes));
            var ctx = new JobContext(jobId, folder, job.Project?.JobNo, recipe, job.Engine, null);
            var (system, user) = PromptBuilder.Build(ctx, job.Project, job.Project?.Client, job.Mail, instruction, PromptBuilder.FindTemplate(o.TemplatesDir, recipe.DocCode));
            var plan = EngineInvocation.Build(recipe, ctx, o, token, system);
            before = folder is null ? null : ProjectFolders.Snapshot(folder);
            agent = agents.Create(job.Engine, $"job-{jobId:N}", plan.WorkDir, plan.ExtraArgs, plan.Env);
            session = job.EngineSession is { } saved
                ? await agent.DeserializeSessionAsync(JsonElement.Parse(saved), cancellationToken: limit.Token)
                : await agent.CreateSessionAsync(limit.Token);

            var final = new StringBuilder();
            var format = new AgentRunOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(PromptBuilder.ResultSchema, "job_result") };
            await foreach (var u in agent.RunStreamingAsync(user, session, format, limit.Token))
            {
                if (u.AdditionalProperties?.TryGetValue("cli.version", out var version) == true) run.EngineVersion = version as string;
                var warning = u.AdditionalProperties?.TryGetValue("cli.kind", out var kind) == true && kind as string == "warning";
                foreach (var c in u.Contents)
                {
                    switch (c)
                    {
                        case TextReasoningContent r when !string.IsNullOrWhiteSpace(r.Text):
                            await Log(warning ? "warning" : "text", r.Text.Trim());
                            break;
                        case TextContent t:
                            final.Append(t.Text);
                            break;
                        case UsageContent usage:
                            var (input, output) = (usage.Details.InputTokenCount ?? 0, usage.Details.OutputTokenCount ?? 0);
                            var cost = u.AdditionalProperties?.TryGetValue("total_cost_usd", out var total) == true ? Convert.ToDecimal(total, CultureInfo.InvariantCulture) : 0;
                            run.InputTokens = (run.InputTokens ?? 0) + input;
                            run.OutputTokens = (run.OutputTokens ?? 0) + output;
                            var line = string.Create(CultureInfo.InvariantCulture, $"입력 토큰 {input:N0}, 출력 토큰 {output:N0}");
                            if (cost > 0)
                            {
                                run.CostUsd = (run.CostUsd ?? 0) + cost;
                                line += string.Create(CultureInfo.InvariantCulture, $", 추정 비용 ${cost:0.00}");
                            }
                            await Log("usage", line);
                            break;
                        case { AdditionalProperties: { } p } when p.TryGetValue("cli.tool", out var tool):
                            await Log("tool", $"{tool} {ArgSummary(p.GetValueOrDefault("cli.args") as string)}".TrimEnd());
                            break;
                    }
                }
                if (await KeepSessionAsync()) await db.SaveChangesAsync(CancellationToken.None);
            }

            job.Result = ParseResult(final.ToString(), folder);
            if (before is not null) await RegisterArtifactsAsync(db, job, recipe.DocCode, folder!, before);
            var unrecorded = recipe.Id == "skill-learning" && !await CommitLearningAsync(db, jobId);
            if (unrecorded) await Log("warning", "스킬 변경을 기록하지 못했습니다. 스킬 파일이 바뀌었는지 확인해 주세요.");
            if (job.Result.Summary.Length > 0) await Log("result", job.Result.Summary);
            // Only this run's questions: one an earlier run left open must not hold every later run back from review.
            var asked = await db.Approvals.AnyAsync(a => a.RunId == run.Id && a.Status == ApprovalStatus.Pending);
            await EndAsync(RunStatus.Done,
                asked ? JobStatus.NeedsAttention : recipe.Id is "reply" or "skill-learning" && !unrecorded ? JobStatus.Done : JobStatus.Review, null);
            await notifier.Notify("작업이 끝났습니다", job.Title, $"/jobs/{jobId}");
        }
        catch (Exception e)
        {
            // The next run's snapshot would already hold what this run wrote, so its files become drafts now or never.
            if (before is not null) await RegisterArtifactsAsync(db, job, recipe!.DocCode, folder!, before);
            if (lifetime.ApplicationStopping.IsCancellationRequested) return; // the next start marks this run stopped
            // The log line first, so whoever sees the job end finds why in its log.
            if (ct.IsCancellationRequested)
            {
                await Approvals.ExpirePendingAsync(db, jobId);
                await Log("system", "작업을 중지했습니다.");
                await EndAsync(RunStatus.Stopped, JobStatus.Canceled, null);
                return;
            }
            var reason = limit.IsCancellationRequested ? $"제한 시간 {recipe!.TimeoutMinutes}분을 넘겨 중지했습니다." : e.Message;
            await Log("error", $"작업이 실패했습니다: {reason}");
            await EndAsync(RunStatus.Failed, JobStatus.Failed, reason);
        }
        finally
        {
            tokens.Revoke(jobId);
            if (codexHeld) agents.CodexSlot.Release();
        }
    }

    /// <summary>Fails the job's working run from a fresh scope after <see cref="RunAsync"/> could not record its end.</summary>
    public async Task FailAsync(Guid jobId, string reason)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var run = await db.JobRuns.Include(r => r.Job).Where(r => r.JobId == jobId && r.Status == RunStatus.Working).OrderByDescending(r => r.Seq).FirstOrDefaultAsync();
        if (run is null) return;
        await JobEventLog.AppendAsync(db, notifier, jobId, run.Id, "error", $"작업이 실패했습니다: {reason}");
        (run.Status, run.Error, run.EndedAt) = (RunStatus.Failed, reason, DateTimeOffset.UtcNow);
        (run.Job.Status, run.Job.UpdatedAt) = (JobStatus.Failed, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));
    }

    /// <summary>Holds the job before its engine starts, with the reason as an error event.</summary>
    public async Task BlockAsync(Guid jobId, string message)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        var run = await NextRunAsync(db, jobId);
        (run.Status, run.Error) = (RunStatus.Blocked, message);
        (job.Status, job.UpdatedAt) = (JobStatus.NeedsAttention, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await JobEventLog.AppendAsync(db, notifier, jobId, run.Id, "error", message);
        await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));
        await notifier.Notify("확인이 필요합니다", message, $"/jobs/{jobId}");
    }

    /// <summary>Cancels a job that has no engine running: queued, or waiting for the CEO.</summary>
    public async Task CancelIdleAsync(Guid jobId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        var runs = await db.JobRuns.Where(r => r.JobId == jobId).OrderBy(r => r.Seq).ToListAsync();
        await Approvals.ExpirePendingAsync(db, jobId);
        if (runs.Count > 0) await JobEventLog.AppendAsync(db, notifier, jobId, runs[^1].Id, "system", "작업을 중지했습니다.");
        foreach (var r in runs.Where(r => r.Status is RunStatus.Blocked or RunStatus.Working)) (r.Status, r.EndedAt) = (RunStatus.Stopped, DateTimeOffset.UtcNow);
        (job.Status, job.UpdatedAt) = (JobStatus.Canceled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));
    }

    // A run that never got past its readiness check is reused, so retries do not pile up runs.
    static async Task<JobRun> NextRunAsync(OfnrDb db, Guid jobId)
    {
        var latest = await db.JobRuns.Where(r => r.JobId == jobId).OrderByDescending(r => r.Seq).FirstOrDefaultAsync();
        return latest is { Status: RunStatus.Blocked } ? latest : db.JobRuns.Add(new JobRun { JobId = jobId, Seq = (latest?.Seq ?? 0) + 1 }).Entity;
    }

    static JobResult ParseResult(string text, string? folder)
    {
        JobResult result;
        try { result = JsonSerializer.Deserialize<JobResult>(text, ResultJson) ?? new() { Summary = text }; }
        catch (JsonException) { return new() { Summary = text.Trim() }; } // an engine that ignored the result schema
        // Engines report absolute paths; files in the project folder are listed relative to it, as artifacts are.
        if (folder is not null)
            result.Files = [.. result.Files.Select(f => Path.IsPathFullyQualified(f) && Path.GetRelativePath(folder, f) is var r && !Path.IsPathRooted(r)
                && r != ".." && !r.StartsWith(".." + Path.DirectorySeparatorChar) ? r.Replace('\\', '/') : f)];
        return result;
    }

    static string ArgSummary(string? json)
    {
        if (json is null) return "";
        try
        {
            var args = JsonElement.Parse(json);
            foreach (var key in ArgKeys)
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) json = v.GetString()!;
        }
        catch (JsonException) { }
        json = string.Join(' ', json.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return json.Length > 120 ? json[..120] + "…" : json;
    }

    /// <summary>
    /// New or changed files named <c>&lt;JobNo&gt;_&lt;DOCCODE&gt;_v&lt;NN&gt;</c> of this project, and of the recipe's document when it
    /// names one, become this job's drafts. Another job of the same project can run at the same time and write its own files.
    /// shortcut: a recipe without a doc code (free) that ends first still takes a file a running job wrote and has not
    /// registered yet; track writes per run if that shows up.
    /// </summary>
    static async Task RegisterArtifactsAsync(OfnrDb db, Job job, string? docCode, string folder, IReadOnlyDictionary<string, (long size, DateTime mtimeUtc)> before)
    {
        var after = ProjectFolders.Snapshot(folder);
        foreach (var path in ProjectFolders.Diff(before, after))
        {
            if (ProjectFolders.ParseVersioned(Path.GetFileName(path)) is not { } v || v.jobNo != job.Project!.JobNo
                || docCode is not null && !v.docCode.Equals(docCode, StringComparison.OrdinalIgnoreCase)) continue;
            var (size, modifiedAt) = (after[path].size, new DateTimeOffset(after[path].mtimeUtc));
            var a = await db.Artifacts.FirstOrDefaultAsync(a => a.ProjectId == job.ProjectId && a.RelativePath == path);
            // Another job's file it has not changed since. The database keeps microseconds, the file system 100 ns.
            if (a is { JobId: { } owner } && owner != job.Id && a.Size == size && (a.ModifiedAt - modifiedAt).Duration() < TimeSpan.FromMilliseconds(1)) continue;
            a ??= db.Artifacts.Add(new Artifact { ProjectId = job.ProjectId!.Value, RelativePath = path }).Entity;
            (a.JobId, a.DocCode, a.Version, a.Status) = (job.Id, v.docCode, v.version, ArtifactStatus.Draft);
            (a.Size, a.ModifiedAt) = (size, modifiedAt);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>False when the plugin keeps a git history and the change did not reach it: the run changed nothing, or git failed.</summary>
    async Task<bool> CommitLearningAsync(OfnrDb db, Guid jobId)
    {
        var f = await db.Feedback.Where(f => f.LearningJobId == jobId).Select(f => new { f.SkillName, f.Job.Project!.JobNo }).FirstOrDefaultAsync();
        return f?.SkillName is not { } name || await skills.CommitLearningAsync(name, f.JobNo ?? DateTimeOffset.Now.ToString("yyyy-MM-dd"));
    }
}
