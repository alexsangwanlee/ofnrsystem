using System.ComponentModel;
using System.Diagnostics;

namespace Ofnr.Server.Ops;

public static class Bootstrap
{
    /// <summary>Creates data folders and copies shipped defaults without touching files the CEO or a skill-learning job already changed.</summary>
    public static void EnsureDataRoot(OfnrOptions o, string contentRoot)
    {
        foreach (var dir in new[] { o.DataRoot, o.JobsRoot, o.PluginDir, o.TemplatesDir, o.BackupsDir, Path.Combine(o.DataRoot, "tmp") })
            Directory.CreateDirectory(dir);
        // ProjectsRoot can sit on an unplugged or read-only drive; Health reports that instead of the server failing to start.
        try { Directory.CreateDirectory(o.ProjectsRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        CopyIfMissing(Path.Combine(contentRoot, "config", "recipes.json"), Path.Combine(o.DataRoot, "recipes.json"));
        CopyTree(Path.Combine(contentRoot, "claude-plugin"), o.PluginDir);
        CopyTree(Path.Combine(contentRoot, "templates"), o.TemplatesDir);

        try
        {
            if (!Directory.Exists(Path.Combine(o.PluginDir, ".git")) && !Git(o.PluginDir, "init")) return;
            // Checked on every start, not only after init, so a first commit that failed once is retried.
            if (Git(o.PluginDir, "rev-parse", "--verify", "--quiet", "HEAD")) return;
            GitCommit(o.PluginDir, "기본 스킬", ".");
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

    static bool Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (p.WaitForExit(TimeSpan.FromSeconds(30))) return p.ExitCode == 0;
        p.Kill(entireProcessTree: true); // a GPG or credential prompt must not keep the server from starting
        return false;
    }
}
