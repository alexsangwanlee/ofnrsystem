using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Ofnr.Server.Projects;

public static partial class ProjectFolders
{
    public static readonly string[] StageFolders = ["00_IN", "01_PLAN", "02_DESIGN", "03_BUILD", "04_OPS", "05_REPORT", "06_ADMIN"];

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"^(OFNR-\d{5})_([A-Za-z0-9]+)_v(\d{2,})(?:_FINAL)?\.[^.]+$")]
    private static partial Regex Versioned();

    /// <summary>ASCII folder short name: Claude Code breaks on Korean paths, and Excel on paths over 218 chars.</summary>
    public static string MakeSlug(string input)
    {
        var slug = NonSlug().Replace(input, "-").Trim('-');
        if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
        return slug.Length == 0 ? "P" : slug;
    }

    public static string Create(OfnrOptions o, string jobNo, string slug, DateTimeOffset createdAt)
    {
        var folder = Path.Combine(o.ProjectsRoot, createdAt.Year.ToString(), $"{jobNo}_{slug}");
        foreach (var stage in StageFolders) Directory.CreateDirectory(Path.Combine(folder, stage));
        return folder;
    }

    /// <summary><c>&lt;jobNo&gt;_&lt;docCode&gt;_v&lt;NN&gt;.&lt;ext&gt;</c> one above the highest version of that document in any extension.</summary>
    public static string NextVersionedName(string dir, string jobNo, string docCode, string ext)
    {
        var max = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(f => ParseVersioned(Path.GetFileName(f)))
                .Where(v => v is { } x && x.jobNo == jobNo && x.docCode == docCode)
                .Select(v => v!.Value.version).DefaultIfEmpty().Max()
            : 0;
        return $"{jobNo}_{docCode}_v{max + 1:D2}.{ext.TrimStart('.')}";
    }

    public static (string jobNo, string docCode, int version)? ParseVersioned(string fileName) =>
        Versioned().Match(fileName) is { Success: true } m && int.TryParse(m.Groups[3].ValueSpan, out var v)
            ? (m.Groups[1].Value, m.Groups[2].Value, v)
            : null;

    /// <summary>Size and mtime of every file, keyed by '/'-separated relative path. Never opens a file, so OneDrive placeholders stay online-only.</summary>
    public static IReadOnlyDictionary<string, (long size, DateTime mtimeUtc)> Snapshot(string root)
    {
        if (!Directory.Exists(root)) return new Dictionary<string, (long, DateTime)>();
        var files = new FileSystemEnumerable<(string, long, DateTime)>(
            root,
            (ref FileSystemEntry e) => (Path.GetRelativePath(root, e.ToFullPath()).Replace('\\', '/'), e.Length, e.LastWriteTimeUtc.UtcDateTime),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !e.FileName.StartsWith("~$"),
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName is not ".git",
        };
        return files.ToDictionary(f => f.Item1, f => (f.Item2, f.Item3));
    }

    public static IReadOnlyList<string> Diff(
        IReadOnlyDictionary<string, (long size, DateTime mtimeUtc)> before,
        IReadOnlyDictionary<string, (long size, DateTime mtimeUtc)> after) =>
        after.Where(f => !before.TryGetValue(f.Key, out var old) || old != f.Value)
            .Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
}
