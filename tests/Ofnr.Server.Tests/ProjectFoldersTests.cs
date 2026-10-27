using Ofnr.Server.Data;
using Ofnr.Server.Projects;

namespace Ofnr.Server.Tests;

public sealed class ProjectFoldersTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string root = Directory.CreateTempSubdirectory("ofnr-f-").FullName;

    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("TNF 성수 팝업 2026!", "TNF-2026")]
    [InlineData("팝업", "P")]
    [InlineData("Samsung Galaxy Unpacked Seoul 2026", "Samsung-Galaxy-Unpacked")]
    public void MakeSlug_keeps_ascii_letters_digits_and_dashes(string input, string expected) =>
        Assert.Equal(expected, ProjectFolders.MakeSlug(input));

    [Fact]
    public void Create_makes_seven_stage_folders()
    {
        var o = new OfnrOptions { ProjectsRoot = root };

        var path = ProjectFolders.Create(o, "OFNR-26031", "TNF-Seongsu", new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(Path.Combine(root, "2026", "OFNR-26031_TNF-Seongsu"), path);
        Assert.Equal(
            ["00_IN", "01_PLAN", "02_DESIGN", "03_BUILD", "04_OPS", "05_REPORT", "06_ADMIN"],
            Directory.GetDirectories(path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NextVersionedName_returns_v01_then_v03_after_v02_exists()
    {
        Assert.Equal("OFNR-26031_QUOTE_v01.xlsx", ProjectFolders.NextVersionedName(root, "OFNR-26031", "QUOTE", "xlsx"));

        foreach (var name in new[] { "OFNR-26031_QUOTE_v01.xlsx", "OFNR-26031_QUOTE_v02.xlsx", "OFNR-26031_PROP_v05.pptx", "OFNR-26030_QUOTE_v09.xlsx" })
            File.WriteAllText(Path.Combine(root, name), "x");

        Assert.Equal("OFNR-26031_QUOTE_v03.xlsx", ProjectFolders.NextVersionedName(root, "OFNR-26031", "QUOTE", ".xlsx"));
    }

    [Fact]
    public void ParseVersioned_reads_job_doc_and_version()
    {
        Assert.Equal(("OFNR-26031", "QUOTE", 3), ProjectFolders.ParseVersioned("OFNR-26031_QUOTE_v03_FINAL.xlsx"));
        Assert.Equal(("OFNR-26031", "PROP", 12), ProjectFolders.ParseVersioned("OFNR-26031_PROP_v12.pptx"));
        Assert.Null(ProjectFolders.ParseVersioned("견적서.xlsx"));
    }

    [Fact]
    public void Snapshot_diff_reports_new_and_modified_but_ignores_office_temp_files()
    {
        Directory.CreateDirectory(Path.Combine(root, "01_PLAN"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var kept = Path.Combine(root, "00_IN", "brief.pdf");
        var modified = Path.Combine(root, "01_PLAN", "OFNR-26031_QUOTE_v01.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "brief");
        File.WriteAllText(modified, "v1");
        var before = ProjectFolders.Snapshot(root);

        File.WriteAllText(modified, "v1 edited");
        File.SetLastWriteTimeUtc(modified, DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(root, "01_PLAN", "OFNR-26031_PROP_v01.pptx"), "new");
        File.WriteAllText(Path.Combine(root, "01_PLAN", "~$OFNR-26031_QUOTE_v01.xlsx"), "lock");
        File.WriteAllText(Path.Combine(root, ".git", "index"), "git");
        var after = ProjectFolders.Snapshot(root);

        Assert.Equal(["00_IN/brief.pdf", "01_PLAN/OFNR-26031_QUOTE_v01.xlsx"], before.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["01_PLAN/OFNR-26031_PROP_v01.pptx", "01_PLAN/OFNR-26031_QUOTE_v01.xlsx"],
            ProjectFolders.Diff(before, after));
    }

    [Fact]
    public async Task JobNumbers_increments_per_year()
    {
        using var t = TestDb.Create();
        await using var db = t.Context();
        var y2026 = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(9));

        foreach (var expected in new[] { "OFNR-26001", "OFNR-26002" })
        {
            var no = await JobNumbers.NextAsync(db, y2026);
            Assert.Equal(expected, no);
            db.Projects.Add(new Project { JobNo = no, Name = "팝업", Slug = "P", FolderPath = "/p/" + no });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal("OFNR-27001", await JobNumbers.NextAsync(db, y2026.AddYears(1)));
    }
}
