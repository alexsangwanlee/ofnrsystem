using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Ofnr.Server.Ops;
using Ofnr.Server.Skills;

namespace Ofnr.Server.Tests;

public sealed class SkillStoreTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly SkillStore store;

    public SkillStoreTests()
    {
        var data = Directory.CreateTempSubdirectory("ofnr-k-").FullName;
        var settings = new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", data)]).Build());
        Bootstrap.EnsureDataRoot(settings.Current, AppContext.BaseDirectory); // the shipped skills, committed as 기본 스킬
        store = new SkillStore(settings);
    }

    static string Quote(string rules) => $"""
        ---
        name: quote
        description: 오프너 견적서(Excel)를 만든다.
        ---

        # 견적서

        - 본문 목록은 규칙이 아니다

        ## 학습된 규칙

        {rules}

        ## 다음 칸

        - 이것도 규칙이 아니다
        """;

    [Fact]
    public async Task List_parses_learned_rules()
    {
        await store.WriteAsync("quote", Quote("- 금액은 천원 단위로 절사한다 (2026-10-27, OFNR-26001)\n- 유효기간은 30일 (2026-10-28, OFNR-26002)"));

        var quote = Assert.Single(store.List(), s => s.Name == "quote");

        Assert.Equal("오프너 견적서(Excel)를 만든다.", quote.Description);
        Assert.Equal(["금액은 천원 단위로 절사한다 (2026-10-27, OFNR-26001)", "유효기간은 30일 (2026-10-28, OFNR-26002)"], quote.LearnedRules);
        Assert.Null(quote.Content);
        Assert.Contains(store.List(), s => s is { Name: "brief", LearnedRules: [] });
    }

    [Fact]
    public async Task Write_rejects_bad_frontmatter()
    {
        var before = store.Read("quote").Content;

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("quote", Quote("").Replace("name: quote", "name: brief")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("quote", Quote("").Replace("description: 오프너 견적서(Excel)를 만든다.", "description:")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("quote", "# 견적서\n\n## 학습된 규칙\n"));

        Assert.Equal(before, store.Read("quote").Content);
    }

    [Fact]
    public async Task Write_commits_and_history_lists_it()
    {
        var written = await store.WriteAsync("quote", Quote("- 금액은 천원 단위로 절사한다"));

        Assert.Equal(["금액은 천원 단위로 절사한다"], written.LearnedRules);
        Assert.Equal(Quote("- 금액은 천원 단위로 절사한다"), store.Read("quote").Content);
        var history = store.History("quote");
        Assert.Equal(["skill(quote): 대표 수정", "기본 스킬"], history.Select(h => h.Message));
        Assert.Matches("^[0-9a-f]{40}$", history[0].Commit);
        Assert.True(history[0].Date > DateTimeOffset.Now.AddMinutes(-5));
        Assert.Equal(["기본 스킬"], store.History("brief").Select(h => h.Message));
    }

    [Fact]
    public async Task Revert_restores_previous_content()
    {
        var shipped = store.Read("quote").Content;
        await store.WriteAsync("quote", Quote("- 첫 규칙"));
        var first = store.History("quote")[^1].Commit;

        var reverted = await store.RevertAsync("quote", first);

        Assert.Equal(shipped, reverted.Content);
        Assert.Equal(shipped, store.Read("quote").Content);
        Assert.Equal($"skill(quote): {first[..7]} 버전으로 되돌림", store.History("quote")[0].Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RevertAsync("quote", "0000000"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RevertAsync("quote", "--help"));
    }

    [Fact]
    public async Task Skill_name_with_path_traversal_is_400()
    {
        Assert.Throws<ArgumentException>(() => store.Read("../x"));
        Assert.Throws<KeyNotFoundException>(() => store.Read("no-such-skill"));

        await using var host = new TestHost();
        var client = host.Authed();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/skills/..%2Fx", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/skills/..%2F..%2Fsettings", new { content = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/skills/no-such-skill", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/skills/quote/history", Ct)).StatusCode);
    }
}
