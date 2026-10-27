using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Skills;

/// <summary>
/// The company skills in <c>&lt;PluginDir&gt;/skills/&lt;name&gt;/SKILL.md</c>, with their history in the plugin's git repository.
/// Unknown or unsafe names throw <see cref="ArgumentException"/> (400), missing skills <see cref="KeyNotFoundException"/> (404).
/// </summary>
public sealed partial class SkillStore(SettingsStore settings)
{
    const string RulesHeading = "## 학습된 규칙";

    // One git writer at a time: the CEO's edit and a skill-learning job's commit would race on the index lock.
    readonly SemaphoreSlim gate = new(1, 1);

    [GeneratedRegex("^[a-z0-9-]+$")]
    public static partial Regex ValidName();

    [GeneratedRegex(@"\A---\r?\n(.*?)\r?\n---(\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex("^[0-9a-f]{7,40}$")]
    private static partial Regex CommitId();

    string Root => settings.Current.PluginDir;

    static string RelativePath(string name) => $"skills/{name}/SKILL.md";

    string FileOf(string name)
    {
        if (!ValidName().IsMatch(name)) throw new ArgumentException("알 수 없는 스킬입니다.");
        var path = Path.Combine(Root, "skills", name, "SKILL.md");
        return File.Exists(path) ? path : throw new KeyNotFoundException("스킬을 찾을 수 없습니다.");
    }

    public SkillDto[] List()
    {
        var dir = Path.Combine(Root, "skills");
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            .Where(n => ValidName().IsMatch(n) && File.Exists(Path.Combine(dir, n, "SKILL.md")))
            .Select(n => Parse(n, File.ReadAllText(Path.Combine(dir, n, "SKILL.md"))) with { Content = null })];
    }

    public SkillDto Read(string name) => Parse(name, File.ReadAllText(FileOf(name)));

    /// <summary>Saves the CEO's edit and commits it. The frontmatter must name this skill and describe it.</summary>
    public async Task<SkillDto> WriteAsync(string name, string content)
    {
        var path = FileOf(name);
        if (Field(content, "name") != name || string.IsNullOrEmpty(Field(content, "description")))
            throw new ArgumentException("스킬 앞머리(--- 사이)에 폴더 이름과 같은 name과 description이 있어야 합니다.");
        await gate.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(path, content);
            Bootstrap.GitCommit(Root, $"skill({name}): 대표 수정", RelativePath(name)); // without git the edit is kept, only unrecorded
        }
        finally { gate.Release(); }
        return Parse(name, content);
    }

    /// <summary>Newest first. Empty when git or the plugin's repository is missing.</summary>
    public SkillHistoryDto[] History(string name)
    {
        FileOf(name);
        string? log;
        try { log = Bootstrap.GitOutput(Root, "log", "--format=%H|%cI|%s", "--", RelativePath(name)); }
        catch (Win32Exception) { return []; }
        return [.. (log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|', 3))
            .Select(p => new SkillHistoryDto(p[0], DateTimeOffset.Parse(p[1], CultureInfo.InvariantCulture), p[2]))];
    }

    public async Task<SkillDto> RevertAsync(string name, string commit)
    {
        FileOf(name);
        if (!CommitId().IsMatch(commit)) throw new ArgumentException("되돌릴 버전을 다시 선택해 주세요.");
        await gate.WaitAsync();
        try
        {
            string? done;
            try { done = Bootstrap.GitOutput(Root, "checkout", commit, "--", RelativePath(name)); }
            catch (Win32Exception) { done = null; }
            if (done is null) throw new KeyNotFoundException("되돌릴 버전을 찾을 수 없습니다.");
            Bootstrap.GitCommit(Root, $"skill({name}): {commit[..7]} 버전으로 되돌림", RelativePath(name));
        }
        finally { gate.Release(); }
        return Read(name);
    }

    /// <summary>Records what a skill-learning job changed. False when the plugin keeps a git history and the change did not reach it.</summary>
    public async Task<bool> CommitLearningAsync(string name, string label)
    {
        await gate.WaitAsync();
        try { return !Directory.Exists(Path.Combine(Root, ".git")) || Bootstrap.GitCommit(Root, $"skill({name}): 학습 {label}", $"skills/{name}"); }
        finally { gate.Release(); }
    }

    static SkillDto Parse(string name, string text) => new(name, Field(text, "description") ?? "", LearnedRules(text), text);

    static string? Field(string text, string key)
    {
        var front = Frontmatter().Match(text);
        if (!front.Success) return null;
        foreach (var line in front.Groups[1].Value.Split('\n'))
            if (line.Split(':', 2) is [var k, var v] && k.Trim() == key) return v.Trim().Trim('"', '\'');
        return null;
    }

    /// <summary>The bullet lines under <c>## 학습된 규칙</c>, up to the next heading.</summary>
    static string[] LearnedRules(string text) =>
    [
        .. text.Split('\n').Select(l => l.Trim()).SkipWhile(l => l != RulesHeading).Skip(1).TakeWhile(l => !l.StartsWith('#'))
            .Where(l => l.StartsWith("- ") || l.StartsWith("* ")).Select(l => l[2..].Trim()),
    ];
}
