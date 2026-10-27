// MAF agents that drive the subscription-authenticated Claude Code and Codex CLIs as AIAgent backends.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ofnr.Server.Agents;

public sealed class CliSession : AgentSession
{
    public CliSession() { }
    public CliSession(AgentSessionStateBag bag) : base(bag) { }
}

/// <summary>Per-run scratch state shared between BuildArgs and Map.</summary>
public sealed class CliRun
{
    public bool Structured;
    public string? SessionId;
    public string? FinalText;
    public string? Error;
}

public abstract class CliAgent(string name, string exe, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env) : AIAgent
{
    const string SessionKey = "cli.session_id";

    // API keys would win over the subscription login; a parent Claude Code session leaks CLAUDECODE and CLAUDE_CODE_*.
    static readonly string[] StrippedEnv = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_PROFILE", "CODEX_API_KEY", "OPENAI_API_KEY", "CLAUDECODE"];

    public override string? Name => name;
    protected override string? IdCore => name; // stable id: workflow checkpoints must rebind to the same executor
    protected string WorkDir => workDir;

    protected abstract IEnumerable<string> BuildArgs(CliRun run, string? resumeId, JsonElement? schema, IReadOnlyList<string> extra);
    protected abstract IEnumerable<AgentResponseUpdate> Map(JsonElement evt, CliRun run);

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default) => new(new CliSession());
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession s, JsonSerializerOptions? o = null, CancellationToken ct = default) => new(s.StateBag.Serialize());
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement e, JsonSerializerOptions? o = null, CancellationToken ct = default) => new(new CliSession(AgentSessionStateBag.Deserialize(e)));

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default)
        => RunCoreStreamingAsync(messages, session, options, ct).ToAgentResponseAsync(ct);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        session ??= await CreateSessionAsync(ct);
        session.StateBag.TryGetValue<string>(SessionKey, out var resumeId);
        JsonElement? schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema; // set by RunAsync<T> or ChatResponseFormat.ForJsonSchema
        var run = new CliRun { Structured = schema is not null };
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in BuildArgs(run, resumeId, schema, extraArgs)) psi.ArgumentList.Add(a);
        foreach (var k in psi.Environment.Keys.Where(k => StrippedEnv.Contains(k, StringComparer.OrdinalIgnoreCase)
            || k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(k);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Start(psi);
        try
        {
            using var kill = ct.Register(() => Kill(p));
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                // Prompt goes through stdin (no command-line length/quoting limits); closing stdin is required.
                await p.StandardInput.WriteAsync(string.Join("\n\n", messages.Select(m => m.Text)));
                p.StandardInput.Close();
            }
            catch (IOException) { } // the CLI exited before reading the prompt; its exit code and stderr say why

            var saved = resumeId;
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(line);
                foreach (var u in Map(doc.RootElement, run)) { u.AgentId = Id; yield return u; }
                // Saved as soon as known, so a follow-up can resume a run that later failed, timed out or was canceled.
                if (run.SessionId is { } sid && sid != saved) session.StateBag.SetValue(SessionKey, saved = sid);
            }
            await p.WaitForExitAsync(ct);
            var err = (await stderr).Trim();
            foreach (var l in err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var w = Warning(l);
                w.AgentId = Id;
                yield return w;
            }
            if (run.Error is not null || p.ExitCode != 0)
            {
                var message = run.Error ?? $"엔진이 종료 코드 {p.ExitCode}로 끝났습니다.";
                if (err.Length > 2000) err = err[^2000..];
                throw new InvalidOperationException(err.Length == 0 ? message : $"{message}\n{err}");
            }
            if (run.FinalText is not null)
                yield return new AgentResponseUpdate(ChatRole.Assistant, run.FinalText) { AgentId = Id };
        }
        finally
        {
            Kill(p);
        }
    }

    static Process Start(ProcessStartInfo psi)
    {
        try { return Process.Start(psi)!; }
        catch (Win32Exception e) { throw new InvalidOperationException($"엔진을 실행하지 못했습니다. 설치와 경로를 확인해 주세요: {psi.FileName}", e); }
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    protected static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Progress text is surfaced as reasoning so AgentResponse.Text holds only the final answer (needed by RunAsync<T>).
    protected static AgentResponseUpdate Progress(string? text) => new(ChatRole.Assistant, [new TextReasoningContent(text)]);

    protected static AgentResponseUpdate Warning(string? text) =>
        new(ChatRole.Assistant, [new TextReasoningContent(text)]) { AdditionalProperties = new() { ["cli.kind"] = "warning" } };

    // CLI-internal tool calls are progress only. Never emit FunctionCallContent for them: a MAF workflow
    // treats an unmatched FunctionCallContent as an external request and pauses waiting for a result.
    protected static AgentResponseUpdate ToolCall(string? id, string? name, JsonElement? args) =>
        new(ChatRole.Assistant, [new AIContent { AdditionalProperties = new() { ["cli.tool_id"] = id, ["cli.tool"] = name, ["cli.args"] = args?.GetRawText() } }]);

    protected static AgentResponseUpdate Usage(long? input, long? output) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })]);
}

/// <summary>claude -p --output-format stream-json (uses the logged-in Claude subscription).</summary>
public sealed class ClaudeCodeAgent(string name, string exe, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env = null)
    : CliAgent(name, exe, workDir, extraArgs, env)
{
    protected override IEnumerable<string> BuildArgs(CliRun run, string? resumeId, JsonElement? schema, IReadOnlyList<string> extra)
    {
        string[] args = ["-p", "--output-format", "stream-json", "--verbose", "--permission-prompts", extra.Contains("--permission-prompt-tool") ? "host" : "none"];
        foreach (var a in args) yield return a;
        if (resumeId is not null) { yield return "--resume"; yield return resumeId; }
        if (schema is { } s) { yield return "--json-schema"; yield return s.GetRawText(); }
        foreach (var a in extra) yield return a; // --plugin-dir, --mcp-config, --permission-mode, ...
    }

    protected override IEnumerable<AgentResponseUpdate> Map(JsonElement e, CliRun run)
    {
        switch (Str(e, "type"))
        {
            case "system" when Str(e, "subtype") == "init":
                run.SessionId = Str(e, "session_id");
                yield return new AgentResponseUpdate { Role = ChatRole.Assistant, AdditionalProperties = new() { ["cli.version"] = Str(e, "claude_code_version") } };
                if (e.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array
                    && servers.EnumerateArray().Any(s => Str(s, "name") == "ofnr" && Str(s, "status") != "connected"))
                    throw new InvalidOperationException("대시보드 MCP에 연결하지 못했습니다");
                break;
            case "assistant":
                foreach (var b in e.GetProperty("message").GetProperty("content").EnumerateArray())
                {
                    var t = Str(b, "type");
                    if (t == "text") yield return Progress(Str(b, "text"));
                    else if (t == "tool_use") yield return ToolCall(Str(b, "id"), Str(b, "name"), b.TryGetProperty("input", out var input) ? input : null);
                }
                break;
            case "result":
                run.SessionId = Str(e, "session_id") ?? run.SessionId;
                if (e.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
                    foreach (var d in denials.EnumerateArray())
                        yield return Warning($"권한 설정으로 도구 사용이 거부되었습니다: {Str(d, "tool_name")}");
                // On auth failure Claude reports subtype "success" with is_error true and exits 1.
                if (e.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    run.Error = $"Claude 실행 오류: {Str(e, "result") ?? Str(e, "subtype")}";
                else if (!run.Structured)
                    run.FinalText = Str(e, "result");
                else if (e.TryGetProperty("structured_output", out var so))
                    run.FinalText = so.GetRawText();
                else
                    run.Error = "Claude가 결과 형식에 맞는 응답을 주지 않았습니다.";
                long? Tokens(string key) => e.TryGetProperty("usage", out var u) && u.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : null;
                var usage = Usage(Tokens("input_tokens"), Tokens("output_tokens"));
                usage.AdditionalProperties = new() { ["total_cost_usd"] = e.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0 };
                yield return usage;
                break;
        }
    }
}

/// <summary>codex exec --json (uses the logged-in ChatGPT account when no API key is in env).</summary>
public sealed class CodexAgent(string name, string exe, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env = null)
    : CliAgent(name, exe, workDir, extraArgs, env)
{
    protected override IEnumerable<string> BuildArgs(CliRun run, string? resumeId, JsonElement? schema, IReadOnlyList<string> extra)
    {
        yield return "exec";
        yield return "--json";
        yield return "--skip-git-repo-check";
        if (schema is { } s)
        {
            var path = Path.Combine(WorkDir, "schema.json"); // kept in the job folder next to the run's other inputs
            File.WriteAllText(path, s.GetRawText());
            yield return "--output-schema"; yield return path;
        }
        foreach (var a in extra) yield return a; // -C, -c sandbox_mode=..., -c mcp_servers..., --add-dir, -m
        // Checked on codex 0.164.0: `exec --json -C /tmp resume <id> -` and `exec resume <id> --json -` both parse,
        // but `-C` and `--add-dir` after `resume` fail with "unexpected argument", so every exec option goes before it.
        if (resumeId is not null) { yield return "resume"; yield return resumeId; }
        yield return "-"; // prompt from stdin
    }

    protected override IEnumerable<AgentResponseUpdate> Map(JsonElement e, CliRun run)
    {
        var type = Str(e, "type");
        switch (type)
        {
            case "thread.started":
                run.SessionId = Str(e, "thread_id");
                break;
            case "item.started" or "item.completed":
                var item = e.GetProperty("item");
                var kind = Str(item, "type");
                if (kind == "agent_message" && type == "item.completed")
                {
                    run.FinalText = Str(item, "text"); // last one wins = final answer
                    yield return Progress(run.FinalText);
                }
                else if (kind is "command_execution" or "mcp_tool_call" && type == "item.started")
                    yield return ToolCall(Str(item, "id"), kind == "mcp_tool_call" ? $"mcp__{Str(item, "server")}__{Str(item, "tool")}" : kind, item);
                else if (kind == "error" && type == "item.completed")
                    yield return Warning(Str(item, "message"));
                break;
            case "turn.completed":
                var u = e.GetProperty("usage");
                yield return Usage(u.GetProperty("input_tokens").GetInt64(), u.GetProperty("output_tokens").GetInt64());
                break;
            case "turn.failed":
                run.Error = $"Codex 실행 오류: {(e.TryGetProperty("error", out var err) ? Str(err, "message") : null)}";
                break;
            case "error": // retry notices such as "Reconnecting... 1/5"; only turn.failed or the exit code means failure
                yield return Warning(Str(e, "message"));
                break;
        }
    }
}
