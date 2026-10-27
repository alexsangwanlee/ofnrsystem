using Microsoft.Extensions.Configuration;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Tests;

public sealed class BootstrapTests
{
    [Fact]
    public void EnsureDataRoot_copies_missing_files_keeps_edited_ones_and_inits_git()
    {
        var root = Directory.CreateTempSubdirectory("ofnr-b-").FullName;
        var content = Path.Combine(root, "content");
        Directory.CreateDirectory(Path.Combine(content, "claude-plugin", "skills", "quote"));
        File.WriteAllText(Path.Combine(content, "claude-plugin", "skills", "quote", "SKILL.md"), "shipped");
        File.WriteAllText(Path.Combine(content, "claude-plugin", "README.md"), "readme");

        var config = new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", Path.Combine(root, "data"))]).Build();
        var o = new SettingsStore(config).Current;
        o.ProjectsRoot = Path.Combine(root, "Projects");
        var edited = Path.Combine(o.PluginDir, "skills", "quote", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(edited)!);
        File.WriteAllText(edited, "learned");

        Bootstrap.EnsureDataRoot(o, content);

        Assert.Equal("learned", File.ReadAllText(edited));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(o.PluginDir, "README.md")));
        Assert.True(Directory.Exists(o.JobsRoot));
        Assert.True(Directory.Exists(o.ProjectsRoot));
        Assert.True(Directory.Exists(Path.Combine(o.PluginDir, ".git")));
    }
}
