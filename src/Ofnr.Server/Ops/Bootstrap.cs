using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ofnr.Server.Ops;

public static class Bootstrap
{
    const string RulesHeading = "\n## 학습된 규칙";

    /// <summary>Creates data folders and brings shipped defaults in without touching what the CEO or a skill-learning job changed.</summary>
    public static void EnsureDataRoot(OfnrOptions o, string contentRoot)
    {
        foreach (var dir in new[] { o.DataRoot, o.JobsRoot, o.PluginDir, o.TemplatesDir, o.BackupsDir, Path.Combine(o.DataRoot, "tmp") })
            Directory.CreateDirectory(dir);
        // ProjectsRoot can sit on an unplugged or read-only drive; Health reports that instead of the server failing to start.
        try { Directory.CreateDirectory(o.ProjectsRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // The copies the running release shipped, so the next release can tell which data files nobody changed.
        var shipped = Path.Combine(o.DataRoot, ".shipped");
        Follow(Path.Combine(contentRoot, "config", "recipes.json"), Path.Combine(o.DataRoot, "recipes.json"), Path.Combine(shipped, "recipes.json"));
        var plugin = Path.Combine(contentRoot, "claude-plugin");
        var updated = false;
        if (Directory.Exists(plugin))
            foreach (var rel in Directory.EnumerateFiles(plugin, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(plugin, f)))
                if (Follow(Path.Combine(plugin, rel), Path.Combine(o.PluginDir, rel), Path.Combine(shipped, "claude-plugin", rel))) updated = true;
        CopyTree(Path.Combine(contentRoot, "templates"), o.TemplatesDir);

        try
        {
            if (!Directory.Exists(Path.Combine(o.PluginDir, ".git")) && !Git(o.PluginDir, "init")) return;
            // Checked on every start, not only after init, so a first commit that failed once is retried.
            if (!Git(o.PluginDir, "rev-parse", "--verify", "--quiet", "HEAD")) GitCommit(o.PluginDir, "기본 스킬", ".");
            else if (updated) GitCommit(o.PluginDir, "앱 업데이트로 받은 기본 스킬", ".");
        }
        catch (Win32Exception) { } // git is not on PATH; skill history stays off until it is installed
    }

    /// <summary>Commits every change under <paramref name="pathspec"/> in the plugin's git history. False when nothing changed or git is missing.</summary>
    public static bool GitCommit(string dir, string message, string pathspec)
    {
        try
        {
            return Git(dir, "add", "-A", "--", pathspec)
                && Git(dir, "-c", "user.name=OFNR", "-c", "user.email=ofnr@localhost", "-c", "commit.gpgsign=false",
                    "commit", "--no-verify", "-m", message, "--", pathspec);
        }
        catch (Win32Exception) { return false; }
    }

    /// <summary>
    /// Copies a shipped file when the data copy is missing or still equals what the last release shipped (<paramref name="last"/>).
    /// A SKILL.md whose only change is its learned rules takes the new text above them and keeps the rules. True when the data copy changed.
    /// </summary>
    static bool Follow(string src, string dst, string last)
    {
        if (!File.Exists(src)) return false;
        var changed = false;
        if (!File.Exists(dst)) changed = CopyOver(src, dst);
        else if (File.Exists(last) && !Same(src, last))
        {
            if (Same(dst, last)) changed = CopyOver(src, dst);
            else if (Path.GetFileName(dst) == "SKILL.md" && Split(File.ReadAllText(dst)) is (var head, var rules)
                && Split(File.ReadAllText(last))?.Head == head && Split(File.ReadAllText(src)) is (var next, _))
            {
                File.WriteAllText(dst, next + rules);
                changed = true;
            }
        }
        CopyOver(src, last);
        return changed;
    }

    static (string Head, string Rules)? Split(string text) => text.IndexOf(RulesHeading, StringComparison.Ordinal) is var i and >= 0 ? (text[..(i + 1)], text[(i + 1)..]) : null;

    static bool Same(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    static bool CopyOver(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        return true;
    }

    static void CopyTree(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            CopyIfMissing(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
    }

    static void CopyIfMissing(string src, string dst)
    {
        if (!File.Exists(src) || File.Exists(dst)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst);
    }

    static bool Git(string dir, params string[] args) => GitOutput(dir, args) is not null;

    /// <summary>Git's standard output, or null when it failed. Throws <see cref="Win32Exception"/> when git is not on PATH.</summary>
    public static string? GitOutput(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (p.WaitForExit(TimeSpan.FromSeconds(30))) return p.ExitCode == 0 ? output.Result : null;
        p.Kill(entireProcessTree: true); // a GPG or credential prompt must not keep the server from starting
        return null;
    }
}
