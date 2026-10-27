using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Ofnr.Server.Agents;
using Ofnr.Server.Jobs;

namespace Ofnr.Server.Tests;

public sealed class CliAgentTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly string Fakes = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fakes"));

    readonly string dir = Directory.CreateTempSubdirectory("ofnr-cli-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    string Log => Path.Combine(dir, "log");

    Dictionary<string, string> Env(string scenario) => new() { ["FAKE_LOG"] = Log, ["FAKE_SCENARIO"] = scenario };

    static string Fake(string name)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are bash scripts, which Windows cannot start.");
        return Path.Combine(Fakes, name);
    }

    AIAgent Claude(string scenario = "ok") => new ClaudeCodeAgent("test", Fake("fake-claude"), dir, [], Env(scenario));

    AIAgent Codex(string scenario = "ok") => new CodexAgent("test", Fake("fake-codex"), dir, ["-C", dir], Env(scenario));

    string[] Args() => File.ReadAllText(Log + ".args").TrimEnd('\0').Split('\0');

    Dictionary<string, string> ChildEnv() => File.ReadAllText(Log + ".env").TrimEnd('\0').Split('\0')
        .Select(l => l.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);

    static AgentRunOptions ResultFormat() => new() { ResponseFormat = ChatResponseFormat.ForJsonSchema(PromptBuilder.ResultSchema, "job_result") };

    static async Task<List<AgentResponseUpdate>> Collect(AIAgent agent, string prompt, AgentSession? session = null, AgentRunOptions? options = null)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync(prompt, session, options, Ct)) updates.Add(u);
        return updates;
    }

    static IEnumerable<string> Warnings(IEnumerable<AgentResponseUpdate> updates) => updates
        .Where(u => u.AdditionalProperties?.TryGetValue("cli.kind", out var k) == true && (string?)k == "warning")
        .SelectMany(u => u.Contents.OfType<TextReasoningContent>()).Select(c => c.Text);

    [Fact]
    public async Task Claude_agent_returns_structured_result_and_session()
    {
        var agent = Claude();
        var session = await agent.CreateSessionAsync(Ct);

        var response = await agent.RunAsync("견적서를 만들어 주세요", session, ResultFormat(), Ct);

        var result = JsonSerializer.Deserialize<JsonElement>(response.Text);
        Assert.Equal("견적서 초안을 만들었습니다", result.GetProperty("summary").GetString());
        Assert.Equal((5400L, 20L), (response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount)); // cache reads and writes count as input
        var args = Args();
        Assert.Equal(PromptBuilder.ResultSchema.GetRawText(), args[Array.IndexOf(args, "--json-schema") + 1]);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none"], args[..6]);
        Assert.Contains("sess-123", (await agent.SerializeSessionAsync(session, cancellationToken: Ct)).GetRawText());
    }

    [Fact]
    public async Task Claude_is_error_true_fails_with_message()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Claude("auth_error").RunAsync("x", cancellationToken: Ct));

        Assert.Contains("Invalid API key. Please run /login", e.Message);
    }

    [Fact]
    public async Task Missing_exe_fails_with_korean_message()
    {
        var agent = new ClaudeCodeAgent("test", Path.Combine(dir, "no-claude"), dir, []);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("x", cancellationToken: Ct));

        Assert.StartsWith("엔진을 실행하지 못했습니다", e.Message);
    }

    [Fact]
    public async Task Codex_error_events_are_warnings_and_turn_completed_succeeds()
    {
        var updates = await Collect(Codex("error_events"), "x", options: ResultFormat());

        Assert.Equal(["Reconnecting... 1/5", "Reconnecting... 2/5"], Warnings(updates));
        // The structured answer is the result only, not also a progress line in the job log.
        Assert.Equal(Warnings(updates), updates.SelectMany(u => u.Contents.OfType<TextReasoningContent>()).Select(c => c.Text));
        var final = updates.Last().Contents.OfType<TextContent>().Single().Text;
        Assert.Equal("요구사항을 정리했습니다", JsonDocument.Parse(final).RootElement.GetProperty("summary").GetString());
        var schema = Path.Combine(dir, "schema.json");
        Assert.Equal(PromptBuilder.ResultSchema.GetRawText(), File.ReadAllText(schema));
        var args = Args();
        Assert.Equal(schema, args[Array.IndexOf(args, "--output-schema") + 1]);
    }

    [Fact]
    public async Task Codex_turn_failed_fails()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Codex("turn_failed").RunAsync("x", cancellationToken: Ct));

        Assert.Contains("stream disconnected before completion", e.Message);
    }

    [Fact]
    public async Task Codex_exit_1_with_empty_stdout_fails_with_stderr()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Codex("exit1_empty").RunAsync("x", cancellationToken: Ct));

        Assert.Contains("required MCP servers failed to initialize: ofnr", e.Message);
    }

    [Fact]
    public async Task Korean_prompt_reaches_stdin_byte_exact()
    {
        foreach (var agent in new[] { Claude(), Codex() })
        {
            await agent.RunAsync("성수 팝업 견적서", cancellationToken: Ct);

            Assert.Equal(Encoding.UTF8.GetBytes("성수 팝업 견적서"), File.ReadAllBytes(Log + ".stdin"));
        }
    }

    [Fact]
    public void Factory_refuses_a_cmd_or_ps1_engine()
    {
        var settings = new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", Path.Combine(dir, "data"))]).Build());
        (settings.Current.ClaudeExe, settings.Current.CodexExe) = (Path.Combine(dir, "claude.cmd"), Path.Combine(dir, "codex.ps1"));

        foreach (var (engine, exe) in new[] { ("claude", settings.Current.ClaudeExe), ("codex", settings.Current.CodexExe) })
            Assert.Equal($"스크립트(.cmd, .ps1)는 서버가 안전하게 실행할 수 없습니다. 설정에서 .exe 파일 경로를 지정해 주세요: {exe}",
                Assert.Throws<InvalidOperationException>(() => new AgentFactory(settings).Create(engine, "test", dir, [])).Message);
    }

    [Fact]
    public async Task Api_keys_and_claudecode_env_are_stripped()
    {
        // OFNR_TOKEN opens the whole /api, mail sending included; OFNR_MAIL_PASSWORD is the mail account's.
        string[] leaked = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_PROFILE", "CODEX_API_KEY", "OPENAI_API_KEY", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT",
            "OFNR_TOKEN", "OFNR_MAIL_PASSWORD"];
        var saved = leaked.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", Path.Combine(dir, "data"))]).Build();
        var settings = new SettingsStore(config);
        settings.Current.ClaudeExe = Fake("fake-claude");
        var gitBash = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        try
        {
            foreach (var k in leaked) Environment.SetEnvironmentVariable(k, "leak");
            Environment.SetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH", @"D:\Git\bin\bash.exe");
            var env = new Dictionary<string, string>(Env("ok")) { ["OFNR_JOB_TOKEN"] = "tok", ["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"] = "0" };

            await new AgentFactory(settings).Create("claude", "test", dir, [], env).RunAsync("x", cancellationToken: Ct);
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH", gitBash);
        }

        var child = ChildEnv();
        Assert.DoesNotContain(child.Keys, leaked.Contains);
        Assert.Equal("0", child["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"]);
        Assert.Equal("tok", child["OFNR_JOB_TOKEN"]);
        Assert.Equal(@"D:\Git\bin\bash.exe", child["CLAUDE_CODE_GIT_BASH_PATH"]);
        Assert.Equal(Path.Combine(dir, "data", "tmp"), child["TEMP"]);
        Assert.Equal(Path.Combine(dir, "data", "tmp"), child["TMP"]);
    }

    [Fact]
    public async Task Resume_passes_session_id()
    {
        var claude = Claude();
        var session = await claude.CreateSessionAsync(Ct);
        await claude.RunAsync("first", session, cancellationToken: Ct);
        Assert.DoesNotContain("--resume", Args());
        // Task 6 stores the session as JSON between runs.
        var restored = await claude.DeserializeSessionAsync(await claude.SerializeSessionAsync(session, cancellationToken: Ct), cancellationToken: Ct);

        await claude.RunAsync("next", restored, cancellationToken: Ct);

        var args = Args();
        Assert.Equal("sess-123", args[Array.IndexOf(args, "--resume") + 1]);

        var codex = Codex();
        var thread = await codex.CreateSessionAsync(Ct);
        await codex.RunAsync("first", thread, cancellationToken: Ct);
        Assert.DoesNotContain("resume", Args());

        await codex.RunAsync("next", thread, cancellationToken: Ct);

        Assert.Equal(["exec", "--json", "--skip-git-repo-check", "-C", dir, "resume", "th-9", "-"], Args());
    }

    [Fact]
    public async Task Ofnr_mcp_not_connected_fails_run()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Claude("mcp_failed").RunAsync("x", cancellationToken: Ct));

        Assert.Contains("대시보드 MCP에 연결하지 못했습니다", e.Message);
    }

    [Fact]
    public async Task Permission_denials_become_warnings()
    {
        var updates = await Collect(Claude("denials"), "x");

        Assert.Equal("2.1.311", updates[0].AdditionalProperties?["cli.version"]);
        Assert.Contains(Warnings(updates), w => w.Contains("Bash"));
    }

    [Fact]
    public async Task Leftover_child_holding_the_pipes_does_not_hold_up_the_run()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15)); // the leftover holds both pipes for 20 seconds
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var response = await Claude("leftover").RunAsync("x", cancellationToken: cts.Token);

        Assert.Equal("done", response.Text);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Malformed_lines_become_warnings()
    {
        var claude = await Collect(Claude("bad_json"), "x");
        var codex = await Collect(Codex("bad_json"), "x", options: ResultFormat());

        Assert.Contains(Warnings(claude), w => w.StartsWith("엔진 출력 한 줄을 읽지 못했습니다: {\"type\":\"system\""));
        Assert.Equal("done", claude.Last().Text);
        Assert.Contains(Warnings(codex), w => w.StartsWith("엔진 출력 한 줄을 읽지 못했습니다: {\"type\":\"item.completed\""));
        Assert.Equal("요구사항을 정리했습니다", JsonDocument.Parse(codex.Last().Text).RootElement.GetProperty("summary").GetString());
    }
}
