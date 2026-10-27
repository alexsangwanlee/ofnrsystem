using System.Text.Json;
using System.Text.Json.Serialization;
using Ofnr.Server.Data;

namespace Ofnr.Server.Jobs;

/// <summary>One kind of AI job, read from <c>&lt;data&gt;/recipes.json</c>. Omitted fields take these defaults.</summary>
public sealed record Recipe
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ProjectStage? Stage { get; init; }
    public string[] Skills { get; init; } = [];
    public string[] McpServers { get; init; } = [];
    public string? Lock { get; init; }
    public string[] Engines { get; init; } = ["claude"];
    public string? DocCode { get; init; }
    public string? OutputDir { get; init; }
    public int TimeoutMinutes { get; init; } = 30;
    public decimal? MaxBudgetUsd { get; init; }
    public bool NeedsProject { get; init; } = true;
    /// <summary>False for recipes whose MCP servers can run arbitrary scripts: their prompt never carries mail text.</summary>
    public bool UntrustedMailInPrompt { get; init; } = true;
    public ClaudeSettings Claude { get; init; } = new();
    public CodexSettings Codex { get; init; } = new();
    public string Prompt { get; init; } = "";
}

/// <summary>dontAsk denies anything not allowlisted: there is no live approval, because Claude backgrounds MCP calls after 2 minutes and aborts idle HTTP MCP calls after 5.</summary>
public sealed record ClaudeSettings
{
    public string PermissionMode { get; init; } = "dontAsk";
    public string[] AllowedTools { get; init; } = ["Read", "Write", "Edit", "Glob", "Grep", "Skill", "TodoWrite", "mcp__ofnr__*"];
    public string[] DisallowedTools { get; init; } = [];
}

public sealed record CodexSettings
{
    public string Sandbox { get; init; } = "workspace-write";
}

public sealed class RecipeCatalog(IReadOnlyList<Recipe> all)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public IReadOnlyList<Recipe> All => all;

    public Recipe? Get(string id) => all.FirstOrDefault(r => r.Id == id);

    public static RecipeCatalog Load(string path)
    {
        Recipe[] recipes;
        try { recipes = JsonSerializer.Deserialize<Recipe[]>(File.ReadAllText(path), Json) ?? []; }
        catch (JsonException e) { throw new InvalidDataException($"recipes.json 형식이 잘못되었습니다: {path}", e); }
        // The CEO and skill-learning jobs edit this file; it must never switch the engines' safety off.
        if (recipes.FirstOrDefault(r => r.Claude.PermissionMode == "bypassPermissions" || r.Codex.Sandbox == "danger-full-access") is { } bad)
            throw new InvalidDataException($"recipes.json의 {bad.Id} 작업에 허용되지 않는 권한 설정이 있습니다: {path}");
        return new(recipes);
    }
}
