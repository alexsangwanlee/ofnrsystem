using System.ComponentModel;
using System.Diagnostics;

namespace Ofnr.Server.Ops;

public static class Bootstrap
{
    /// <summary>Creates data folders and copies shipped defaults without touching files the CEO or a skill-learning job already changed.</summary>
    public static void EnsureDataRoot(OfnrOptions o, string contentRoot)
    {
        foreach (var dir in new[] { o.DataRoot, o.ProjectsRoot, o.JobsRoot, o.PluginDir, o.TemplatesDir, o.BackupsDir, Path.Combine(o.DataRoot, "tmp") })
            Directory.CreateDirectory(dir);

        CopyIfMissing(Path.Combine(contentRoot, "config", "recipes.json"), Path.Combine(o.DataRoot, "recipes.json"));
        CopyTree(Path.Combine(contentRoot, "claude-plugin"), o.PluginDir);
        CopyTree(Path.Combine(contentRoot, "templates"), o.TemplatesDir);

        if (!Directory.Exists(Path.Combine(o.PluginDir, ".git")))
        {
            try
            {
                Git(o.PluginDir, "init");
                Git(o.PluginDir, "add", "-A");
                Git(o.PluginDir, "-c", "user.name=OFNR", "-c", "user.email=ofnr@localhost", "commit", "-m", "기본 스킬");
            }
            catch (Win32Exception) { } // git is not on PATH; skill history stays off until it is installed
        }
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

    static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        stderr.Wait();
        p.WaitForExit();
    }
}
