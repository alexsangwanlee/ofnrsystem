using Microsoft.Agents.AI;

namespace Ofnr.Server.Agents;

/// <summary>Builds the CLI agent for one run. Virtual so queue and triage tests can return a scripted agent.</summary>
public class AgentFactory(SettingsStore settings)
{
    /// <summary>One Codex process on this PC at a time (parallel ones race on refreshing the login token). Jobs and mail triage hold it around each Codex run.</summary>
    public SemaphoreSlim CodexSlot { get; } = new(1, 1);

    /// <param name="env">Extra child environment, such as <see cref="Jobs.EnginePlan.Env"/>.</param>
    public virtual AIAgent Create(string engine, string name, string workDir, IReadOnlyList<string> extraArgs, IReadOnlyDictionary<string, string>? env = null)
    {
        var o = settings.Current;
        // cmd.exe would re-parse the arguments, and they carry project text that can come from a client's mail.
        if (Ops.Health.ScriptError(engine == "codex" ? o.CodexExe : o.ClaudeExe) is { } error) throw new InvalidOperationException(error);
        // ASCII temp folder even when the Windows user name is Korean.
        var tmp = Path.Combine(o.DataRoot, "tmp");
        var childEnv = new Dictionary<string, string>(env ?? new Dictionary<string, string>()) { ["TEMP"] = tmp, ["TMP"] = tmp };
        return engine switch
        {
            "claude" => new ClaudeCodeAgent(name, o.ClaudeExe, workDir, extraArgs, childEnv),
            "codex" => new CodexAgent(name, o.CodexExe, workDir, extraArgs, childEnv),
            _ => throw new ArgumentException($"알 수 없는 엔진입니다: {engine}", nameof(engine)),
        };
    }
}
