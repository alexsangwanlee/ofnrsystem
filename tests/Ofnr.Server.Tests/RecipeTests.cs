using Ofnr.Server.Data;
using Ofnr.Server.Jobs;

namespace Ofnr.Server.Tests;

public sealed class RecipeTests
{
    // The shipped copies the server build places next to the test assembly.
    static readonly string Recipes = Path.Combine(AppContext.BaseDirectory, "config", "recipes.json");
    static readonly string Skills = Path.Combine(AppContext.BaseDirectory, "claude-plugin", "skills");

    [Fact]
    public void Recipes_json_has_twelve_valid_recipes()
    {
        var catalog = RecipeCatalog.Load(Recipes);

        (string id, string name, ProjectStage? stage, string? docCode, string? outputDir, string? lockName, int timeout)[] expected =
        [
            ("brief", "요구사항 정리", ProjectStage.Inquiry, "BRIEF", "01_PLAN", null, 30),
            ("reply", "회신 초안", ProjectStage.Inquiry, null, null, null, 30),
            ("quote", "견적서", ProjectStage.Planning, "QUOTE", "01_PLAN", "office", 30),
            ("proposal", "제안서", ProjectStage.Planning, "PROP", "01_PLAN", "office", 30),
            ("document", "문서", ProjectStage.Planning, "DOC", "01_PLAN", null, 30),
            ("contract", "계약서", ProjectStage.Planning, "CONTRACT", "01_PLAN", null, 30),
            ("ops-manual", "운영 매뉴얼", ProjectStage.Operation, "OPSMANUAL", "04_OPS", null, 30),
            ("model3d", "3D 모델", ProjectStage.Design, "MODEL", "02_DESIGN/3D", "sketchup", 60),
            ("graphic", "그래픽", ProjectStage.Design, "KV", "02_DESIGN/KV", "illustrator", 60),
            ("report", "결과보고서", ProjectStage.Report, "REPORT", "05_REPORT", "office", 30),
            ("skill-learning", "스킬 학습", null, null, null, null, 30),
            ("free", "자유 지시", null, null, null, null, 30),
        ];
        Assert.Equal(expected, catalog.All.Select(r => (r.Id, r.Name, r.Stage, r.DocCode, r.OutputDir, r.Lock, r.TimeoutMinutes)));

        foreach (var r in catalog.All)
        {
            Assert.All(r.Skills, s => Assert.True(File.Exists(Path.Combine(Skills, s, "SKILL.md")), $"{r.Id}: skill {s}"));
            Assert.NotEmpty(r.Engines);
            Assert.All(r.Engines, e => Assert.Contains(e, new[] { "claude", "codex" }));
            Assert.Equal("dontAsk", r.Claude.PermissionMode);
            Assert.Equal("workspace-write", r.Codex.Sandbox);
            Assert.Equal(r.Id is not ("skill-learning" or "free"), r.NeedsProject);
            // A shell or a script-running app is not held to the job's folders, so those recipes never read mail.
            var shell = r.Claude.AllowedTools.Contains("Bash");
            Assert.Equal(r.Lock is not ("sketchup" or "illustrator" or "photoshop") && !shell, r.UntrustedMailInPrompt);
            Assert.Equal(!shell, r.Claude.DisallowedTools.Contains("Bash"));
            if (shell) Assert.All(r.Skills, s => Assert.DoesNotContain("get_mail_thread", File.ReadAllText(Path.Combine(Skills, s, "SKILL.md"))));
            Assert.DoesNotContain(r.Name + r.Prompt, c => c is '\u2013' or '\u2014');
        }
        Assert.Equal(["brief", "free", "reply", "skill-learning"], catalog.All.Where(r => r.Engines.Contains("codex")).Select(r => r.Id).Order());
        // .docx has no MCP server: these write it with the Anthropic docx skill's scripts.
        Assert.Equal(["contract", "document", "ops-manual", "report"], catalog.All.Where(r => r.Claude.AllowedTools.Contains("Bash")).Select(r => r.Id).Order());
        Assert.Equal(["excel"], catalog.Get("quote")!.McpServers);
        Assert.Equal(["sketchup"], catalog.Get("model3d")!.McpServers);
        Assert.Equal(["Read", "Write", "Edit", "Glob", "Grep", "Skill", "TodoWrite", "mcp__ofnr__*"], catalog.Get("brief")!.Claude.AllowedTools);
        Assert.Empty(catalog.Get("free")!.Skills);
        Assert.Null(catalog.Get("nope"));
    }

    [Theory]
    [InlineData("""[{ "id": "quote", "name": "견적서", "claude": { "allowedTools": ["Read", "Bash"], "disallowedTools": [] } }]""", false)]
    [InlineData("""[{ "id": "quote", "name": "견적서", "claude": { "allowedTools": ["Bash(python:*)"] } }]""", false)]
    [InlineData("""[{ "id": "quote", "name": "견적서", "untrustedMailInPrompt": false, "claude": { "allowedTools": ["Read", "Bash"], "disallowedTools": [] } }]""", true)]
    public void A_recipe_that_allows_bash_must_keep_mail_out(string json, bool loads)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);

            if (loads) Assert.Single(RecipeCatalog.Load(path).All);
            else Assert.Throws<InvalidDataException>(() => RecipeCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Bypass_permission_mode_is_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{ "id": "free", "name": "자유 지시", "engines": ["claude"], "claude": { "permissionMode": "bypassPermissions" } }]""");

            Assert.Throws<InvalidDataException>(() => RecipeCatalog.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
