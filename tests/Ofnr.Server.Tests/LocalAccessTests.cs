using System.Net;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class LocalAccessTests(TestHost host) : IClassFixture<TestHost>
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_without_token_is_401()
    {
        var res = await host.CreateClient().GetAsync("/api/health", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Health_with_token_is_200()
    {
        var res = await host.Authed().GetAsync("/api/health", Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"dataRoot\"", await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Forged_host_is_403()
    {
        var client = host.Authed();
        client.DefaultRequestHeaders.Host = "evil.example:47821";
        var res = await client.GetAsync("/api/health", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Token_in_query_is_accepted_only_for_hubs()
    {
        var client = host.CreateClient();
        var api = await client.GetAsync($"/api/health?access_token={LocalAccess.Token}", Ct);
        var hubWithout = await client.PostAsync("/hubs/ops/negotiate", null, Ct);
        var hubWith = await client.PostAsync($"/hubs/ops/negotiate?access_token={LocalAccess.Token}", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hubWithout.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, hubWith.StatusCode);
    }

    [Fact]
    public async Task Cors_preflight_from_unknown_origin_has_no_allow_origin_header()
    {
        var res = await host.CreateClient().SendAsync(Preflight("https://evil.example"), Ct);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_allows_tauri_localhost()
    {
        var res = await host.CreateClient().SendAsync(Preflight("http://tauri.localhost"), Ct);
        Assert.Equal("http://tauri.localhost", Assert.Single(res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    static HttpRequestMessage Preflight(string origin) => new(HttpMethod.Options, "/api/health")
    {
        Headers =
        {
            { "Origin", origin },
            { "Access-Control-Request-Method", "GET" },
            { "Access-Control-Request-Headers", "x-ofnr-token" },
        },
    };
}
