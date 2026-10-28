using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Jobs;

public interface IJobQueue
{
    /// <summary>Wakes the queue for a job saved as <see cref="JobStatus.Queued"/>.</summary>
    Task EnqueueAsync(Guid jobId);

    /// <summary>Stops the job's engine or takes it out of the queue, and returns once it is <see cref="JobStatus.Canceled"/>.</summary>
    Task CancelAsync(Guid jobId);

    /// <summary>Queues the next run of the job with this instruction; while the job runs, once that run ends.</summary>
    Task FollowupAsync(Guid jobId, string instruction);

    IReadOnlyList<LockDto> Locks { get; }
}

/// <summary>
/// Starts queued jobs in FIFO order among those allowed to start: at most MaxConcurrentJobs runs, one per app lock, one Codex
/// run at a time (parallel Codex processes race on refreshing the login token). Queued jobs live in the database, so they
/// survive a restart; startup recovery stops the runs the previous process left behind.
/// </summary>
public sealed class JobQueue(IServiceScopeFactory scopes, SettingsStore settings, RecipeCatalog recipes, JobRunner runner, IOpsNotifier notifier,
    ILogger<JobQueue> logger) : BackgroundService, IJobQueue
{
    static readonly string[] LockNames = ["office", "sketchup", "illustrator", "photoshop"];

    // An engine started in the project folder would load these, and a client's zip can carry them.
    static readonly string[] AgentConfigNames = [".claude", ".mcp.json", ".codex", ".agents", "CLAUDE.md", "AGENTS.md"];

    static readonly TimeSpan Poll = TimeSpan.FromSeconds(30), RetryEvery = TimeSpan.FromMinutes(2), RetryFor = TimeSpan.FromMinutes(30);

    sealed class Active(string? lockName, string engine, CancellationTokenSource cts)
    {
        public string? Lock => lockName;
        public string Engine => engine;
        public CancellationTokenSource Cts => cts;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    sealed record Retry(string Lock, DateTimeOffset Next, DateTimeOffset Until);

    // All three are guarded by gate, as is Job.PendingInstruction.
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<Guid, Active> running = [];
    readonly Dictionary<Guid, Retry> retries = [];

    readonly SemaphoreSlim signal = new(0);
    bool recovered;
    IReadOnlyList<LockDto> locks = [.. LockNames.Select(n => new LockDto(n, null, 0))];

    public IReadOnlyList<LockDto> Locks => locks;

    public Task EnqueueAsync(Guid jobId)
    {
        Signal();
        return Task.CompletedTask;
    }

    public async Task CancelAsync(Guid jobId)
    {
        Task? run = null;
        await gate.WaitAsync();
        try
        {
            retries.Remove(jobId);
            using (var scope = scopes.CreateScope()) // a canceled job drops what it held for the next run
                await scope.ServiceProvider.GetRequiredService<OfnrDb>().Jobs.Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.PendingInstruction, (string?)null));
            if (running.TryGetValue(jobId, out var a))
            {
                a.Cts.Cancel(); // the runner kills the engine and records the cancel
                run = a.Task;
            }
            else await runner.CancelIdleAsync(jobId);
        }
        finally { gate.Release(); }
        if (run is not null) await run;
    }

    public async Task FollowupAsync(Guid jobId, string instruction)
    {
        await gate.WaitAsync();
        try
        {
            if (running.ContainsKey(jobId))
            {
                // In the database: the tray's 종료 kills the server mid-run, and an answered question cannot be answered again.
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
                var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
                job.PendingInstruction = job.PendingInstruction is { } earlier ? $"{earlier}\n\n{instruction}" : instruction;
                await db.SaveChangesAsync();
                return;
            }
            retries.Remove(jobId);
            await QueueFollowupAsync(jobId, instruction);
        }
        finally { gate.Release(); }
        Signal();
    }

    /// <summary>Recovery runs before the server takes requests, so it never mistakes a new run for one the last process left.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await RecoverAsync(cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Job recovery failed; the queue retries it"); }
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken); // cancels every run through the stopping token
        Task[] runs;
        await gate.WaitAsync(cancellationToken);
        try { runs = [.. running.Values.Select(a => a.Task)]; }
        finally { gate.Release(); }
        await Task.WhenAll(runs).WaitAsync(cancellationToken); // lets each engine's process tree be killed
    }

    /// <summary>
    /// For the start of the process only: runs still Working or Blocked belong to a server that stopped, so they become Stopped
    /// and their jobs NeedsAttention, or Queued with the follow-up they held. Queued jobs start again; pending questions stay pending.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var stale = await db.JobRuns.Include(r => r.Job).Where(r => r.Status == RunStatus.Working || r.Status == RunStatus.Blocked).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var r in stale)
        {
            (r.Status, r.EndedAt) = (RunStatus.Stopped, now);
            // A blocked job that got a follow-up is queued again and stays queued.
            if (r.Job.Status is JobStatus.Running or JobStatus.NeedsAttention) (r.Job.Status, r.Job.UpdatedAt) = (JobStatus.NeedsAttention, now);
        }
        await db.SaveChangesAsync(ct);
        foreach (var r in stale.Where(r => r.Job.Status == JobStatus.NeedsAttention))
        {
            if (r.Job.PendingInstruction is not null) await QueueFollowupAsync(r.JobId, null);
            else await JobEventLog.AppendAsync(db, notifier, r.JobId, r.Id, "warning", "서버가 다시 시작되어 작업이 멈췄습니다. 후속 지시로 이어서 진행하세요.", ct);
        }
        recovered = true;
        Signal();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start before the web server, and a job's MCP address needs the port it binds.
        await settings.Listening.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!recovered) await RecoverAsync(stoppingToken);
                await DispatchAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // An unreachable database must not stop the server; the next pass tries again.
                logger.LogError(e, "Job queue pass failed");
            }
            try { await signal.WaitAsync(Poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    void Signal()
    {
        if (signal.CurrentCount == 0) signal.Release();
    }

    async Task DispatchAsync(CancellationToken stopping)
    {
        await gate.WaitAsync(stopping);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
            var now = DateTimeOffset.UtcNow;
            foreach (var (id, r) in retries.Where(r => r.Value.Next <= now).ToList())
            {
                if (now > r.Until) retries.Remove(id);
                else if (await AppReadiness.CheckAsync(r.Lock, stopping) is not null) retries[id] = r with { Next = now + RetryEvery };
                else
                {
                    retries.Remove(id);
                    await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.NeedsAttention)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.UpdatedAt, now), stopping);
                }
            }

            var queued = await db.Jobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.UpdatedAt)
                .Select(j => new { j.Id, j.RecipeId, j.Engine, j.Instruction, Folder = j.Project == null ? null : j.Project.FolderPath })
                .ToListAsync(stopping);
            var waiting = new List<string?>();
            foreach (var j in queued.Where(j => !running.ContainsKey(j.Id)))
            {
                var lockName = recipes.Get(j.RecipeId)?.Lock;
                if (running.Count >= Math.Max(1, settings.Current.MaxConcurrentJobs)
                    || lockName is not null && running.Values.Any(a => a.Lock == lockName)
                    || j.Engine == "codex" && running.Values.Any(a => a.Engine == "codex"))
                {
                    waiting.Add(lockName);
                    continue;
                }
                var config = j.Folder is null ? [] : AgentConfigNames.Where(n => Path.Exists(Path.Combine(j.Folder, n))).ToArray();
                if (config.Length > 0)
                    await runner.BlockAsync(j.Id, $"프로젝트 폴더에 AI 설정 파일이 있어 실행을 멈췄습니다. 확인 후 지워 주세요: {string.Join(", ", config)}");
                else if (lockName is not null && await AppReadiness.CheckAsync(lockName, stopping) is { } notReady)
                {
                    await runner.BlockAsync(j.Id, notReady);
                    retries[j.Id] = new(lockName, now + RetryEvery, now + RetryFor);
                }
                else Start(j.Id, j.Instruction, lockName, j.Engine, stopping);
            }
            locks = [.. LockNames.Select(n => new LockDto(
                n, running.Where(a => a.Value.Lock == n).Select(a => (Guid?)a.Key).FirstOrDefault(), waiting.Count(w => w == n)))];
        }
        finally { gate.Release(); }
    }

    void Start(Guid jobId, string instruction, string? lockName, string engine, CancellationToken stopping)
    {
        var a = new Active(lockName, engine, CancellationTokenSource.CreateLinkedTokenSource(stopping));
        running[jobId] = a;
        a.Task = Task.Run(async () =>
        {
            try { await runner.RunAsync(jobId, instruction, a.Cts.Token); }
            catch (Exception e)
            {
                logger.LogError(e, "Job {JobId} could not record its end", jobId);
                // Once more on a fresh connection, so the job does not show Running with no engine until the next start.
                try { if (!stopping.IsCancellationRequested) await runner.FailAsync(jobId, e.Message); }
                catch (Exception again) { logger.LogError(again, "Job {JobId} stays running until the next start", jobId); }
            }
            await EndedAsync(jobId, a);
            a.Cts.Dispose();
        }, CancellationToken.None);
    }

    async Task EndedAsync(Guid jobId, Active a)
    {
        await gate.WaitAsync();
        try
        {
            running.Remove(jobId);
            // On an app stop the held text stays for the next start's recovery.
            if (!a.Cts.IsCancellationRequested) await QueueFollowupAsync(jobId, null);
        }
        catch (Exception e) { logger.LogError(e, "Job {JobId} follow-up could not be queued", jobId); }
        finally { gate.Release(); }
        Signal();
    }

    /// <summary>Queues the next run with what the job held (<see cref="Job.PendingInstruction"/>) and then <paramref name="instruction"/>; does nothing when both are empty.</summary>
    async Task QueueFollowupAsync(Guid jobId, string? instruction)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        instruction = string.Join("\n\n", new[] { job.PendingInstruction, instruction }.Where(t => !string.IsNullOrEmpty(t)));
        if (instruction.Length == 0) return;
        job.PendingInstruction = null;
        // The request the engine never got stays in front: no engine session yet, or the last run was held before its engine started.
        var undelivered = job.Status == JobStatus.Queued || job.EngineSession is null
            || await db.JobRuns.Where(r => r.JobId == jobId).OrderByDescending(r => r.Seq).Select(r => r.StartedAt).FirstOrDefaultAsync() is null;
        job.Instruction = undelivered ? $"{job.Instruction}\n\n{instruction}".Trim() : instruction;
        (job.Status, job.UpdatedAt) = (JobStatus.Queued, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await notifier.JobUpdated(await JobEndpoints.DtoAsync(db, recipes, jobId));
    }
}
