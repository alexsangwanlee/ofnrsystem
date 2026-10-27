using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Ofnr.Server.Agents;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

/// <summary>One agent run as the job runner started it.</summary>
public sealed class FakeCall(string engine, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env)
{
    public string Engine => engine;
    public string WorkDir => workDir;
    /// <summary>Empty for runs outside a job folder, such as mail triage.</summary>
    public Guid JobId { get; } = Guid.TryParseExact(Path.GetFileName(workDir)[2..], "N", out var id) ? id : Guid.Empty;
    public IReadOnlyList<string> ExtraArgs => extraArgs;
    public IReadOnlyDictionary<string, string> Env => env ?? new Dictionary<string, string>();
    public string Prompt { get; set; } = "";
    public JsonElement? Schema { get; set; }
    public string? ResumedSession { get; set; }
    public Guid[] RunningWith { get; set; } = [];
    public bool Canceled { get; set; }
}

/// <summary>Agents that play a script instead of starting a CLI. While <see cref="Hold"/> is set, runs wait for <see cref="ReleaseAll"/>.</summary>
public sealed class FakeAgents() : AgentFactory(null!)
{
    public const string Result = """{"summary":"완료했습니다","files":["01_PLAN/x.md"],"needs_confirmation":["면적"],"next_steps":["견적서"]}""";

    readonly Lock gate = new();
    readonly List<FakeCall> calls = [], active = [];
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Replaces <see cref="Default"/> for the next runs.</summary>
    public Func<FakeCall, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>? Script { get; set; }
    public bool Hold { get; set; }
    public int MaxActive { get; private set; }
    public int MaxActiveCodex { get; private set; }
    public List<FakeCall> Calls { get { lock (gate) return [.. calls]; } }
    public List<FakeCall> Active { get { lock (gate) return [.. active]; } }

    public void ReleaseAll() => release.TrySetResult();

    public override AIAgent Create(string engine, string name, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env = null) =>
        new ScriptedAgent(this, new FakeCall(engine, workDir, extraArgs, env));

    /// <summary>Engine version, a progress line, a tool call, usage, then <see cref="Result"/>.</summary>
    public static async IAsyncEnumerable<AgentResponseUpdate> Default(FakeCall call, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new AgentResponseUpdate { Role = ChatRole.Assistant, AdditionalProperties = new() { ["cli.version"] = "2.1.311" } };
        yield return new(ChatRole.Assistant, [new TextReasoningContent("자료를 읽고 있습니다")]);
        yield return new(ChatRole.Assistant, [new AIContent { AdditionalProperties = new() { ["cli.tool"] = "Write", ["cli.args"] = """{"file_path":"/p/brief.md","content":"긴 내용"}""" } }]);
        yield return new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 1200, OutputTokenCount = 340 })])
        {
            AdditionalProperties = new() { ["total_cost_usd"] = 0.42 },
        };
        yield return new(ChatRole.Assistant, Result);
    }

    void Enter(FakeCall call)
    {
        lock (gate)
        {
            call.RunningWith = [.. active.Select(a => a.JobId)];
            calls.Add(call);
            active.Add(call);
            MaxActive = Math.Max(MaxActive, active.Count);
            MaxActiveCodex = Math.Max(MaxActiveCodex, active.Count(a => a.Engine == "codex"));
        }
    }

    void Exit(FakeCall call)
    {
        lock (gate) active.Remove(call);
    }

    sealed class ScriptedAgent(FakeAgents fake, FakeCall call) : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default) => new(new CliSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession s, JsonSerializerOptions? o = null, CancellationToken ct = default) => new(s.StateBag.Serialize());
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement e, JsonSerializerOptions? o = null, CancellationToken ct = default) => new(new CliSession(AgentSessionStateBag.Deserialize(e)));

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default)
            => RunCoreStreamingAsync(messages, session, options, ct).ToAgentResponseAsync(ct);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            session ??= new CliSession();
            session.StateBag.TryGetValue<string>(CliAgent.SessionKey, out var resumed);
            call.ResumedSession = resumed;
            call.Prompt = string.Join("\n\n", messages.Select(m => m.Text));
            call.Schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
            session.StateBag.SetValue(CliAgent.SessionKey, resumed ?? "sess-" + call.JobId.ToString("N"));
            fake.Enter(call);
            try
            {
                if (fake.Hold) await fake.release.Task.WaitAsync(ct);
                await foreach (var u in (fake.Script ?? Default)(call, ct).WithCancellation(ct)) yield return u;
            }
            finally
            {
                call.Canceled = ct.IsCancellationRequested;
                fake.Exit(call);
            }
        }
    }
}

/// <summary>A server whose jobs run on <see cref="FakeAgents"/>. One per test, so the run counts start at zero.</summary>
public sealed class JobHost : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly TestHost host = new();
    readonly Action<IServiceCollection>? services;

    /// <param name="services">Extra test services, such as fakes for the mail server.</param>
    public JobHost(Action<IServiceCollection>? services = null)
    {
        this.services = services;
        File.WriteAllText(Path.Combine(DataRoot, "mcp.json"), """
            { "mcpServers": { "excel": { "command": "excel-mcp" }, "powerpoint": { "command": "ppt-mcp" },
              "sketchup": { "command": "sketchup-mcp" }, "illustrator": { "command": "illustrator-mcp" } } }
            """);
        // The shipped recipes plus one whose time limit is over as soon as it starts.
        var recipes = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "recipes.json")))!.AsArray();
        recipes.Add(JsonNode.Parse("""{ "id": "slow", "name": "느린 작업", "engines": ["claude"], "needsProject": false, "timeoutMinutes": 0 }"""));
        File.WriteAllText(Path.Combine(DataRoot, "recipes.json"), recipes.ToJsonString());
        App = Start();
    }

    public FakeAgents Agents { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public WebApplicationFactory<Program> App { get; private set; }

    WebApplicationFactory<Program> Start()
    {
        var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<AgentFactory>(Agents).AddSingleton<IOpsNotifier>(Notifier);
            services?.Invoke(s);
        }));
        _ = app.Services; // starts the server, which migrates the database
        return app;
    }

    /// <summary>Stops the server the way closing the app does, then starts a new one on the same database and data folder.</summary>
    public async Task RestartAsync()
    {
        await App.DisposeAsync();
        App = Start();
    }
    public string DataRoot => host.DataRoot;

    public OfnrDb Db() => new(new DbContextOptionsBuilder<OfnrDb>().UseNpgsql(host.ConnectionString).Options);

    public async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var client = App.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:47821") });
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);
        var res = await client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        Assert.True(res.StatusCode == expected, $"{method} {path}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    public async Task<(Guid Id, string Folder, string JobNo)> Project(string name = "성수 팝업")
    {
        var p = await Send(HttpMethod.Post, "/api/projects", new { name });
        return (p.GetProperty("id").GetGuid(), p.GetProperty("folderPath").GetString()!, p.GetProperty("jobNo").GetString()!);
    }

    public async Task<Guid> Job(string recipeId, Guid? projectId = null, string instruction = "진행해 주세요", string? engine = null) =>
        (await Send(HttpMethod.Post, "/api/jobs", new { recipeId, projectId, instruction, engine })).GetProperty("id").GetGuid();

    public async Task<Job> Get(Guid jobId)
    {
        await using var db = Db();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
    }

    public async Task<List<JobRun>> Runs(Guid jobId)
    {
        await using var db = Db();
        return await db.JobRuns.AsNoTracking().Where(r => r.JobId == jobId).OrderBy(r => r.Seq).ToListAsync(Ct);
    }

    public async Task<List<JobEvent>> Events(Guid jobId)
    {
        await using var db = Db();
        return await db.JobEvents.AsNoTracking().Where(e => e.JobId == jobId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    public async Task<Job> WaitForStatus(Guid jobId, JobStatus status)
    {
        Job? job = null;
        await WaitFor(async () => (job = await Get(jobId)).Status == status, () => $"job is {job?.Status}, expected {status}");
        return job!;
    }

    public static Task WaitFor(Func<bool> done) => WaitFor(() => Task.FromResult(done()), () => "condition");

    static async Task WaitFor(Func<Task<bool>> done, Func<string> what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await done())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting: " + what());
            await Task.Delay(50, Ct);
        }
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
