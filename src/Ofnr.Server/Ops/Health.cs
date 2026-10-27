using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Agents;
using Ofnr.Server.Data;
using Ofnr.Server.Mail;

namespace Ofnr.Server.Ops;

public sealed record CheckDto(string Name, bool Ok, string Detail);

/// <param name="Name">claude or codex, also the {engine} of POST /api/health/engine-check/{engine}.</param>
/// <param name="Ok">The CLI runs and, once an engine check ran, that check passed. A version other than <paramref name="Pinned"/> is only a warning in <paramref name="Detail"/>.</param>
public sealed record EngineDto(string Name, string Path, string? Version, string Pinned, bool Ok, bool LoggedIn, string? Detail);

public sealed record HealthDto(string Version, string DataRoot, string ProjectsRoot, CheckDto Db, CheckDto Mail, EngineDto[] Engines, CheckDto[] Mcp, CheckDto[] Paths);

public sealed partial class Health(SettingsStore settings, IMailSync mail, Backup backup, IServiceScopeFactory scopes)
{
    static readonly (string Name, string Pinned, string[] Login, string LoginHelp)[] Engines =
    [
        ("claude", "2.1.311", ["auth", "status"], "PowerShell에서 claude를 실행하고 /login으로 로그인해 주세요."),
        ("codex", "0.164.0", ["login", "status"], "PowerShell에서 codex login을 실행해 주세요."),
    ];

    readonly ConcurrentDictionary<string, EngineDto> engines = new(); // last probe, with the last engine check applied
    readonly ConcurrentDictionary<string, CheckDto> checks = new();

    /// <summary>Why the startup migration did not run, shown while migrations are still pending.</summary>
    public string? MigrateError { get; set; }

    public async Task<HealthDto> BuildAsync(CancellationToken ct = default)
    {
        var o = settings.Current;
        var probes = EnginesAsync(fresh: true);
        return new(typeof(Health).Assembly.GetName().Version!.ToString(3), o.DataRoot, o.ProjectsRoot, await DbAsync(ct), mail.Health,
            await probes, Mcp(o.DataRoot), [.. Paths(o), Git(o.DataRoot), backup.Health]);
    }

    /// <summary>Probes each CLI; with <paramref name="fresh"/> false, an engine probed before keeps its last result.</summary>
    public async Task<EngineDto[]> EnginesAsync(bool fresh) =>
        await Task.WhenAll(Engines.Select(e => !fresh && engines.TryGetValue(e.Name, out var last) ? Task.FromResult(last) : ProbeAsync(e.Name)));

    /// <summary>Keeps an engine check's verdict and returns the engine as health shows it from now on.</summary>
    public Task<EngineDto> RecordCheckAsync(string engine, bool ok, string detail)
    {
        checks[engine] = new(engine, ok, detail);
        return ProbeAsync(engine);
    }

    async Task<EngineDto> ProbeAsync(string engine)
    {
        var o = settings.Current;
        var (_, pinned, login, loginHelp) = Engines.Single(e => e.Name == engine);
        var exe = engine == "claude" ? o.ClaudeExe : o.CodexExe;
        EngineDto dto;
        if (Script(exe) is { } script)
        {
            dto = new(engine, script, null, pinned, false, false, ScriptError(exe));
        }
        else
        {
            try
            {
                var (exit, output) = await RunAsync(exe, "--version");
                var version = exit == 0 ? VersionNumber().Match(output) is { Success: true } m ? m.Value : null : null;
                var loggedIn = (await RunAsync(exe, login)).Exit == 0;
                string?[] notes = [loggedIn ? null : $"로그인이 필요합니다. {loginHelp}", version != pinned ? $"고정 버전 {pinned}과 다릅니다. 엔진 점검으로 동작을 확인해 주세요." : null];
                dto = new(engine, exe, version, pinned, version is not null, loggedIn,
                    version is null ? $"실행 파일이 버전을 알려 주지 않았습니다: {exe}" : string.Join(' ', notes.OfType<string>()) is { Length: > 0 } note ? note : null);
            }
            catch (Exception e) when (e is Win32Exception or TimeoutException)
            {
                dto = new(engine, exe, null, pinned, false, false, $"실행 파일을 실행하지 못했습니다. 설치와 설정의 실행 파일 경로를 확인해 주세요: {exe}");
            }
        }
        if (checks.TryGetValue(engine, out var c)) dto = dto with { Ok = dto.Ok && c.Ok, Detail = c.Ok ? dto.Detail ?? c.Detail : c.Detail };
        return engines[engine] = dto;
    }

    static readonly string[] Scripts = [".cmd", ".bat", ".ps1"];

    /// <summary>
    /// The script <paramref name="exe"/> runs, or null for a program. Process.Start hands a .cmd to cmd.exe, which re-parses
    /// the arguments (prompt, paths), and cannot start a .ps1. A bare name runs name.exe from PATH, as Windows does, and only
    /// an npm shim (name.cmd, name.ps1) without one is a script.
    /// </summary>
    static string? Script(string exe)
    {
        if (Scripts.Contains(Path.GetExtension(exe), StringComparer.OrdinalIgnoreCase)) return exe;
        if (!OperatingSystem.IsWindows() || Path.HasExtension(exe) || Path.GetDirectoryName(exe) is { Length: > 0 }) return null;
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return path.Any(d => File.Exists(Path.Combine(d, exe + ".exe"))) ? null
            : path.SelectMany(d => Scripts.Select(x => Path.Combine(d, exe + x))).FirstOrDefault(File.Exists);
    }

    /// <summary>Why <paramref name="exe"/> must not run, for health and for every engine run; null for a program.</summary>
    internal static string? ScriptError(string exe) =>
        Script(exe) is { } script ? $"스크립트(.cmd, .ps1)는 서버가 안전하게 실행할 수 없습니다. 설정에서 .exe 파일 경로를 지정해 주세요: {script}" : null;

    /// <exception cref="TimeoutException">No exit within 10 seconds.</exception>
    static async Task<(int Exit, string Output)> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        CliAgent.StripEnv(psi); // jobs run without API keys, so login status must too
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException();
        }
        return (p.ExitCode, await output);
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();

    /// <summary>Live: PostgreSQL can stop or start after the server did.</summary>
    async Task<CheckDto> DbAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        try
        {
            var pending = await scope.ServiceProvider.GetRequiredService<OfnrDb>().Database.GetPendingMigrationsAsync(ct);
            return pending.Any()
                ? new("데이터베이스", false, MigrateError ?? "데이터베이스 업데이트가 남아 있습니다. 앱을 다시 시작하면 적용됩니다.")
                : new("데이터베이스", true, "연결됨");
        }
        catch (Exception e) when (e is DbException or InvalidOperationException)
        {
            return new("데이터베이스", false, $"데이터베이스에 연결하지 못했습니다: {e.Message}");
        }
    }

    /// <summary>One check per server in <c>&lt;data&gt;/mcp.json</c>: an absolute command must exist. A hand-edited file of another shape is a failing check, never an error.</summary>
    static CheckDto[] Mcp(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "mcp.json");
        if (!File.Exists(path)) return [new("mcp.json", false, $"외부 MCP 설정 파일이 없습니다. 설치 스크립트(setup.ps1)를 다시 실행해 주세요: {path}")];
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException e) { return [new("mcp.json", false, $"mcp.json 형식이 잘못되었습니다: {e.Message}")]; }
        if ((root as JsonObject)?["mcpServers"] is not JsonObject servers) return [new("mcp.json", false, "mcp.json에 mcpServers 항목이 없습니다.")];
        return [.. servers.Select(s => (s.Value as JsonObject)?["command"] is JsonValue c && c.TryGetValue(out string? command)
            ? Path.IsPathRooted(command) && !File.Exists(command) ? new CheckDto(s.Key, false, $"실행 파일이 없습니다: {command}") : new CheckDto(s.Key, true, command)
            : (s.Value as JsonObject)?["url"] is JsonValue u && u.TryGetValue(out string? url) ? new CheckDto(s.Key, true, url)
            : new CheckDto(s.Key, false, "설정 형식이 잘못되었습니다. command 또는 url이 있어야 합니다."))];
    }

    // Skill history needs git; without it skills still work.
    static CheckDto Git(string dir)
    {
        try { return Bootstrap.GitOutput(dir, "--version") is { } v ? new("Git", true, v.Trim()) : throw new Win32Exception(); }
        catch (Win32Exception) { return new("Git", false, "Git을 찾을 수 없습니다. 스킬 변경 이력을 남기려면 Git for Windows를 설치해 주세요."); }
    }

    public static CheckDto[] Paths(OfnrOptions o) => [ProjectsFolder(o.ProjectsRoot), AsciiPath("작업 폴더", o.JobsRoot)];

    // Bootstrap lets the server start without this folder, so a missing drive shows up here.
    static CheckDto ProjectsFolder(string path)
    {
        if (!Directory.Exists(path)) return new("프로젝트 폴더", false, $"폴더를 만들 수 없습니다. 드라이브 연결을 확인하거나 설정에서 다른 폴더를 지정해 주세요: {path}");
        try { using (File.Create(Path.Combine(path, $".ofnr-{Guid.NewGuid():N}.tmp"), 1, FileOptions.DeleteOnClose)) { } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new("프로젝트 폴더", false, $"폴더에 쓸 수 없습니다. 권한을 확인해 주세요: {path}"); }
        return AsciiPath("프로젝트 폴더", path);
    }

    // The engine CLIs break on non-ASCII paths, so a Korean folder name is an error, not a warning.
    static CheckDto AsciiPath(string name, string path) => path.All(char.IsAscii)
        ? new(name, true, path)
        : new(name, false, $"경로에 영문이 아닌 문자가 있습니다. 영문 경로로 바꿔 주세요: {path}");
}
