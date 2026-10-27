using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Tests;

public sealed class BootstrapTests
{
    readonly string root = Directory.CreateTempSubdirectory("ofnr-b-").FullName;

    string Content => Path.Combine(root, "content");

    OfnrOptions Options()
    {
        Directory.CreateDirectory(Path.Combine(Content, "claude-plugin", "skills", "quote"));
        File.WriteAllText(Path.Combine(Content, "claude-plugin", "skills", "quote", "SKILL.md"), "shipped");
        File.WriteAllText(Path.Combine(Content, "claude-plugin", "README.md"), "readme");

        var config = new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", Path.Combine(root, "data"))]).Build();
        var o = new SettingsStore(config).Current;
        o.ProjectsRoot = Path.Combine(root, "Projects");
        return o;
    }

    [Fact]
    public void EnsureDataRoot_copies_missing_files_keeps_edited_ones_and_inits_git()
    {
        var o = Options();
        var edited = Path.Combine(o.PluginDir, "skills", "quote", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(edited)!);
        File.WriteAllText(edited, "learned");

        Bootstrap.EnsureDataRoot(o, Content);

        Assert.Equal("learned", File.ReadAllText(edited));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(o.PluginDir, "README.md")));
        Assert.True(Directory.Exists(o.JobsRoot));
        Assert.True(Directory.Exists(o.ProjectsRoot));
        Assert.Equal("기본 스킬", Git(o.PluginDir, "log", "--format=%s"));
    }

    [Fact]
    public void EnsureDataRoot_retries_first_commit_despite_gpgsign_and_failing_hook()
    {
        var o = Options();
        Directory.CreateDirectory(o.PluginDir);
        // The state an earlier failed first commit leaves behind: .git exists but has no commits.
        Git(o.PluginDir, "init");
        Git(o.PluginDir, "config", "commit.gpgsign", "true");
        Git(o.PluginDir, "config", "gpg.program", "false");
        var hook = Path.Combine(o.PluginDir, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Bootstrap.EnsureDataRoot(o, Content);

        Assert.Equal("기본 스킬", Git(o.PluginDir, "log", "--format=%s"));
    }

    [Fact]
    public void EnsureDataRoot_survives_an_unusable_projects_root_and_health_reports_it()
    {
        var o = Options();
        var blocker = Path.Combine(root, "not-a-folder");
        File.WriteAllText(blocker, "");
        o.ProjectsRoot = Path.Combine(blocker, "Projects");

        Bootstrap.EnsureDataRoot(o, Content);

        var check = Assert.Single(Health.Build(o, null).Paths, c => c.Name == "프로젝트 폴더");
        Assert.False(check.Ok);
        Assert.Contains(o.ProjectsRoot, check.Detail);
    }

    static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }
}
