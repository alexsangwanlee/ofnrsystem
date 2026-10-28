using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ofnr.Server.Jobs;

public sealed record JobContext(Guid JobId, string? ProjectFolder, string? JobNo, Recipe Recipe, string Engine, string? ResumeId);

/// <param name="SchemaPath">Where CodexAgent writes the result schema for this run; null for Claude, which takes it inline.</param>
public sealed record EnginePlan(string WorkDir, IReadOnlyList<string> ExtraArgs, IReadOnlyDictionary<string, string> Env, string? SchemaPath);

/// <summary>Per-job CLI arguments, MCP config and skill copies. The job's MCP token only ever travels in the child environment.</summary>
public static class EngineInvocation
{
    // Dashboard MCP tools (Task 5). Codex cancels MCP calls under approval_policy="never" unless each tool is pre-approved (openai/codex#24135).
    static readonly string[] OfnrTools = ["get_project", "get_mail_thread", "save_reply_draft", "report_progress", "ask_ceo"];

    // Recipes that keep mail out of the prompt (Recipe.UntrustedMailInPrompt) must not fetch it through this tool either.
    const string MailTool = "get_mail_thread";

    // Runs arbitrary Ruby with file, network and shell access, and the 3D job reads client files (00_IN attachments, BRIEF).
    const string SketchupEval = "mcp__sketchup__eval_ruby";

    const int McpTimeoutSec = 1800;

    public static EnginePlan Build(Recipe r, JobContext ctx, OfnrOptions o, string mcpToken, string system)
    {
        // The whole id: a v7 id starts with its timestamp, and any 8 hex of it collide within a few thousand jobs.
        var work = Path.Combine(o.JobsRoot, "j-" + ctx.JobId.ToString("N"));
        Directory.CreateDirectory(work);
        var writable = r.Id == "skill-learning" ? Path.Combine(o.PluginDir, "skills") : ctx.ProjectFolder;
        var servers = RecipeServers(r, o);
        var ofnrUrl = $"http://127.0.0.1:{o.Port}/mcp";
        var env = new Dictionary<string, string> { ["OFNR_JOB_TOKEN"] = mcpToken };
        var args = new List<string>();

        if (ctx.Engine == "claude")
        {
            var mcp = new JsonObject
            {
                // Claude Code expands ${OFNR_JOB_TOKEN} from the child environment, so the token never touches disk.
                ["ofnr"] = new JsonObject { ["type"] = "http", ["url"] = ofnrUrl, ["headers"] = new JsonObject { ["Authorization"] = "Bearer ${OFNR_JOB_TOKEN}" } },
            };
            foreach (var (name, server) in servers) mcp[name] = server.DeepClone();
            foreach (var (_, server) in mcp) server!["timeout"] = McpTimeoutSec * 1000;
            var mcpPath = Path.Combine(work, "mcp.json");
            File.WriteAllText(mcpPath, new JsonObject { ["mcpServers"] = mcp }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Bare Write/Edit reach any path, such as <data>\mcp.json, whose commands every later job starts. Claude Code checks
            // every file write against Edit(path) rules only; a Write(path) rule matches nothing and prints a warning.
            string[] scopes = [.. new[] { writable, work }.OfType<string>().Distinct().Select(PathRule)];
            var allowed = r.Claude.AllowedTools.SelectMany(t => t is "Write" or "Edit" ? scopes.Select(s => $"Edit({s})") : [t]).Distinct();
            args.AddRange(["--plugin-dir", o.PluginDir, "--mcp-config", mcpPath, "--strict-mcp-config", "--permission-mode", r.Claude.PermissionMode,
                "--allowedTools", string.Join(",", allowed.Concat(r.McpServers.Select(s => $"mcp__{s}__*")))]);
            string[] disallowed = r.UntrustedMailInPrompt ? r.Claude.DisallowedTools : [.. r.Claude.DisallowedTools, "mcp__ofnr__" + MailTool];
            if (r.McpServers.Contains("sketchup")) disallowed = [.. disallowed, SketchupEval];
            if (disallowed.Length > 0) args.AddRange(["--disallowedTools", string.Join(",", disallowed)]);
            args.AddRange(["--append-system-prompt", system]);
            if (writable is not null) args.AddRange(["--add-dir", writable]);
            if (r.MaxBudgetUsd is { } budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
            if (o.ClaudeModel is { Length: > 0 } model) args.AddRange(["--model", model]);
            // Set after CliAgent strips CLAUDE_CODE_*: keeps long MCP calls (SketchUp, Office) in the foreground.
            env["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"] = "0";
            return new(work, args, env, null);
        }
        if (ctx.Engine != "codex") throw new ArgumentException($"알 수 없는 엔진입니다: {ctx.Engine}", nameof(ctx));

        // Codex finds skills under <cwd>/.agents/skills; the skills point at ../../knowledge.
        CopyDir(Path.Combine(o.PluginDir, "knowledge"), Path.Combine(work, ".agents", "knowledge"));
        foreach (var skill in r.Skills) CopyDir(Path.Combine(o.PluginDir, "skills", skill), Path.Combine(work, ".agents", "skills", skill));

        // A JSON value is valid TOML, so -c never needs hand-written quoting. TOML rejects the surrogate-pair escapes
        // JsonSerializer writes for characters outside the BMP (an emoji), so those are dropped.
        void C(string key, object value) => args.AddRange(["-c", $"{key}={JsonSerializer.Serialize(value is string s ? string.Concat(s.EnumerateRunes().Where(x => x.IsBmp)) : value)}"]);
        void Common(string server)
        {
            C($"mcp_servers.{server}.default_tools_approval_mode", "approve");
            C($"mcp_servers.{server}.tool_timeout_sec", McpTimeoutSec);
            C($"mcp_servers.{server}.startup_timeout_sec", 30);
            C($"mcp_servers.{server}.required", true);
        }
        args.AddRange(["--ignore-user-config", "-C", work]);
        C("sandbox_mode", r.Codex.Sandbox);
        C("approval_policy", "never");
        C("developer_instructions", system);
        C("mcp_servers.ofnr.url", ofnrUrl);
        C("mcp_servers.ofnr.bearer_token_env_var", "OFNR_JOB_TOKEN");
        Common("ofnr");
        foreach (var tool in OfnrTools) C($"mcp_servers.ofnr.tools.{tool}.approval_mode", "approve");
        if (!r.UntrustedMailInPrompt) C("mcp_servers.ofnr.disabled_tools", new[] { MailTool });
        foreach (var (name, server) in servers)
        {
            C($"mcp_servers.{name}.command", (string)server["command"]!);
            if (server["args"] is JsonArray a) C($"mcp_servers.{name}.args", a.Select(x => (string)x!).ToArray());
            if (server["env"] is JsonObject e) foreach (var (k, v) in e) C($"mcp_servers.{name}.env.{k}", (string)v!);
            Common(name);
        }
        if (writable is not null) args.AddRange(["--add-dir", writable]);
        if (o.CodexModel is { Length: > 0 } codexModel) args.AddRange(["-m", codexModel]);
        return new(work, args, env, Path.Combine(work, "schema.json"));
    }

    /// <summary>Claude Code's rule for everything under an absolute folder: <c>//tmp/x/**</c>, and <c>C:\x</c> as <c>//c/x/**</c>.</summary>
    static string PathRule(string dir)
    {
        var full = Path.GetFullPath(dir).Replace('\\', '/').TrimEnd('/');
        return "/" + (full.Length > 1 && full[1] == ':' ? "/" + char.ToLowerInvariant(full[0]) + full[2..] : full) + "/**";
    }

    /// <summary>The recipe's external servers from <c>&lt;data&gt;/mcp.json</c> (Claude's <c>mcpServers</c> format).</summary>
    static List<(string name, JsonNode server)> RecipeServers(Recipe r, OfnrOptions o)
    {
        if (r.McpServers.Length == 0) return [];
        var path = Path.Combine(o.DataRoot, "mcp.json");
        var all = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] as JsonObject : null;
        return r.McpServers.Select(name => (name, all?[name] ?? throw new InvalidOperationException(
            $"MCP 서버 설정이 없습니다: {name}. 설치 스크립트로 MCP 서버를 등록해 주세요."))).ToList();
    }

    static void CopyDir(string src, string dst)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true); // learned rules change between runs
        }
    }
}
