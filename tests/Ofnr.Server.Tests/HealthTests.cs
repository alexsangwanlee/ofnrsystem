using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Ofnr.Server.Data;
using Ofnr.Server.Ops;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class HealthTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Fake(string name)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLIs are bash scripts, which Windows cannot start.");
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fakes", name));
    }

    static JsonElement Engine(JsonElement health, string name) =>
        Assert.Single(health.GetProperty("engines").EnumerateArray(), e => e.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Database_down_keeps_server_running_with_failing_db_check()
    {
        await using var host = new TestHost();
        var app = host.WithWebHostBuilder(b => b.UseSetting("OFNR_DB", "Host=127.0.0.1;Port=1;Database=ofnr_none;Username=ofnr;Password=ofnr_dev"));
        var client = app.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:47821") });
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);

        var h = await client.GetFromJsonAsync<JsonElement>("/api/health", Ct);

        Assert.False(h.GetProperty("db").GetProperty("ok").GetBoolean());
        Assert.StartsWith("데이터베이스에 연결하지 못했습니다: ", h.GetProperty("db").GetProperty("detail").GetString());

        // PostgreSQL comes up after the server did: the startup connection error is stale, and a restart applies the update.
        app.Services.GetRequiredService<SettingsStore>().Current.ConnectionString = host.ConnectionString;
        var db = (await client.GetFromJsonAsync<JsonElement>("/api/health", Ct)).GetProperty("db");
        Assert.Equal((false, "데이터베이스 업데이트가 남아 있습니다. 앱을 다시 시작하면 적용됩니다."), (db.GetProperty("ok").GetBoolean(), db.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Health_reports_engines_mcp_and_paths()
    {
        await using var host = new TestHost();
        var settings = host.Services.GetRequiredService<SettingsStore>().Current;
        (settings.ClaudeExe, settings.CodexExe) = (Fake("fake-claude"), Fake("fake-codex"));
        File.WriteAllText(Path.Combine(host.DataRoot, "mcp.json"), """
            { "mcpServers": { "excel": { "command": "/nowhere/mcp-excel.exe" }, "remote": { "type": "http", "url": "http://127.0.0.1:9/mcp" } } }
            """);

        var h = await host.Authed().GetFromJsonAsync<JsonElement>("/api/health", Ct);

        Assert.True(h.GetProperty("db").GetProperty("ok").GetBoolean());
        var claude = Engine(h, "claude");
        Assert.Equal(("2.1.311", "2.1.311", true, true), (claude.GetProperty("version").GetString(), claude.GetProperty("pinned").GetString(),
            claude.GetProperty("ok").GetBoolean(), claude.GetProperty("loggedIn").GetBoolean()));
        var codex = Engine(h, "codex");
        Assert.Equal(("0.163.0", "0.164.0", true, false), (codex.GetProperty("version").GetString(), codex.GetProperty("pinned").GetString(),
            codex.GetProperty("ok").GetBoolean(), codex.GetProperty("loggedIn").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, claude.GetProperty("detail").ValueKind);
        Assert.Equal("로그인이 필요합니다. PowerShell에서 codex login을 실행해 주세요. 고정 버전 0.164.0과 다릅니다. 엔진 점검으로 동작을 확인해 주세요.",
            codex.GetProperty("detail").GetString());
        Assert.Equal([("excel", false), ("remote", true)], h.GetProperty("mcp").EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("ok").GetBoolean())));
        Assert.Contains(h.GetProperty("paths").EnumerateArray(), c => c.GetProperty("name").GetString() == "Git" && c.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Mcp_json_of_an_unexpected_shape_is_a_failing_check()
    {
        await using var host = new TestHost();
        var path = Path.Combine(host.DataRoot, "mcp.json");
        CheckDto[] Mcp(JsonElement h) => [.. h.GetProperty("mcp").EnumerateArray().Select(c => new CheckDto(c.GetProperty("name").GetString()!, c.GetProperty("ok").GetBoolean(), c.GetProperty("detail").GetString()!))];

        File.WriteAllText(path, """{ "mcpServers": { "excel": "mcp-excel.exe", "remote": { "url": "http://127.0.0.1:9/mcp" }, "empty": {} } }""");
        Assert.Equal([new("excel", false, "설정 형식이 잘못되었습니다. command 또는 url이 있어야 합니다."), new("remote", true, "http://127.0.0.1:9/mcp"),
            new("empty", false, "설정 형식이 잘못되었습니다. command 또는 url이 있어야 합니다.")], Mcp(await host.Authed().GetFromJsonAsync<JsonElement>("/api/health", Ct)));

        File.WriteAllText(path, "[1, 2]");
        Assert.Equal([new("mcp.json", false, "mcp.json에 mcpServers 항목이 없습니다.")], Mcp(await host.Authed().GetFromJsonAsync<JsonElement>("/api/health", Ct)));
    }

    [Fact]
    public async Task Engine_set_to_a_cmd_or_ps1_shim_is_an_error()
    {
        await using var host = new TestHost();
        var settings = host.Services.GetRequiredService<SettingsStore>().Current;
        (settings.ClaudeExe, settings.CodexExe) = (Path.Combine(host.DataRoot, "claude.cmd"), Path.Combine(host.DataRoot, "codex.ps1"));

        var h = await host.Authed().GetFromJsonAsync<JsonElement>("/api/health", Ct);

        foreach (var (name, exe) in new[] { ("claude", settings.ClaudeExe), ("codex", settings.CodexExe) })
        {
            var engine = Engine(h, name);
            Assert.False(engine.GetProperty("ok").GetBoolean());
            Assert.Equal($"스크립트(.cmd, .ps1)는 서버가 안전하게 실행할 수 없습니다. 설정에서 .exe 파일 경로를 지정해 주세요: {exe}", engine.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task Engine_check_passes_only_when_the_engine_reports_through_mcp()
    {
        await using var h = new JobHost();
        h.App.Services.GetRequiredService<SettingsStore>().Current.ClaudeExe = Fake("fake-claude");

        var silent = await h.Send(HttpMethod.Post, "/api/health/engine-check/claude");
        Assert.Equal(("claude", false), (silent.GetProperty("name").GetString(), silent.GetProperty("ok").GetBoolean()));
        Assert.Equal("엔진이 대시보드 MCP 도구를 쓰지 못했습니다. 엔진의 MCP 설정과 승인 설정을 확인해 주세요.", silent.GetProperty("detail").GetString());

        // The engine starts the tool call, which never reaches the server (Codex cancels it, openai/codex#24135), and writes the line itself.
        h.Agents.Script = (_, ct) => Echo(ct);
        var echoed = await h.Send(HttpMethod.Post, "/api/health/engine-check/claude");
        Assert.Equal((false, "엔진이 대시보드 MCP 도구를 쓰지 못했습니다. 엔진의 MCP 설정과 승인 설정을 확인해 주세요."),
            (echoed.GetProperty("ok").GetBoolean(), echoed.GetProperty("detail").GetString()));

        h.Agents.Script = (call, ct) => ReportThroughMcp(h, call, ct);
        var passed = await h.Send(HttpMethod.Post, "/api/health/engine-check/claude");
        Assert.Equal((true, "엔진 점검을 통과했습니다."), (passed.GetProperty("ok").GetBoolean(), passed.GetProperty("detail").GetString()));
        Assert.Contains("mcp__ofnr__report_progress", h.Agents.Calls[^1].Prompt);
        Assert.Equal("엔진 점검을 통과했습니다.", Engine(await h.Send(HttpMethod.Get, "/api/health"), "claude").GetProperty("detail").GetString());

        await using var db = h.Db();
        Assert.All(await db.Jobs.ToListAsync(Ct), j => Assert.Equal(("free", JobStatus.Done), (j.RecipeId, j.Status)));
        await h.Send(HttpMethod.Post, "/api/health/engine-check/gemini", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Failed_engine_check_is_reported_and_leaves_no_failed_job()
    {
        await using var h = new JobHost();
        h.Agents.Script = (_, _) => throw new InvalidOperationException("Not logged in");

        var failed = await h.Send(HttpMethod.Post, "/api/health/engine-check/claude");

        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.StartsWith("엔진 점검이 실패했습니다: ", failed.GetProperty("detail").GetString());
        await using var db = h.Db();
        Assert.Equal(JobStatus.Done, (await db.Jobs.SingleAsync(Ct)).Status);
        Assert.Empty((await h.Send(HttpMethod.Get, "/api/today")).GetProperty("attention").EnumerateArray());
    }

    [Fact]
    public async Task Engine_check_that_does_not_end_in_time_fails_and_cancels_its_job()
    {
        await using var h = new JobHost();
        h.Agents.Hold = true;
        var timeout = OpsEndpoints.CheckTimeout;
        OpsEndpoints.CheckTimeout = TimeSpan.FromSeconds(1);
        JsonElement late;
        try { late = await h.Send(HttpMethod.Post, "/api/health/engine-check/claude"); }
        finally { OpsEndpoints.CheckTimeout = timeout; }

        Assert.False(late.GetProperty("ok").GetBoolean());
        Assert.EndsWith("안에 끝나지 않았습니다.", late.GetProperty("detail").GetString());
        await using var db = h.Db();
        await h.WaitForStatus((await db.Jobs.SingleAsync(Ct)).Id, JobStatus.Canceled);
    }

    static async IAsyncEnumerable<AgentResponseUpdate> Echo([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(1, ct);
        yield return new(ChatRole.Assistant, [new AIContent { AdditionalProperties = new() { ["cli.tool"] = "mcp__ofnr__report_progress", ["cli.args"] = """{"message":"엔진 점검 완료"}""" } }]);
        yield return new(ChatRole.Assistant, [new TextReasoningContent("엔진 점검 완료")]);
        yield return new(ChatRole.Assistant, FakeAgents.Result);
    }

    static async IAsyncEnumerable<AgentResponseUpdate> ReportThroughMcp(JobHost h, FakeCall call, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new(ChatRole.Assistant, [new AIContent { AdditionalProperties = new() { ["cli.tool"] = "mcp__ofnr__report_progress", ["cli.args"] = """{"message":"엔진 점검 완료"}""" } }]);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://127.0.0.1:47821/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + call.Env["OFNR_JOB_TOKEN"] },
        }, h.App.CreateClient(), ownsHttpClient: true);
        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: ct))
            await client.CallToolAsync("report_progress", new Dictionary<string, object?> { ["message"] = "엔진 점검 완료" }, cancellationToken: ct);
        yield return new(ChatRole.Assistant, FakeAgents.Result);
    }

    [Fact]
    public async Task Settings_hide_connection_string_and_reject_bad_values()
    {
        await using var host = new TestHost();
        var client = host.Authed();

        var s = await client.GetFromJsonAsync<JsonElement>("/api/settings", Ct);
        Assert.False(s.TryGetProperty("connectionString", out _));
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.GetRawText())!;
        edited["maxConcurrentJobs"] = JsonSerializer.SerializeToElement(3);
        edited["connectionString"] = JsonSerializer.SerializeToElement("Host=evil.example");
        edited["mail"] = JsonSerializer.SerializeToElement(new { address = "ceo@ofnr.kr", imapHost = "imap.worksmobile.com", imapPort = 993, smtpHost = "smtp.worksmobile.com", smtpPort = 465 });

        var saved = await (await client.PutAsJsonAsync("/api/settings", edited, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((3, "ceo@ofnr.kr"), (saved.GetProperty("maxConcurrentJobs").GetInt32(), saved.GetProperty("mail").GetProperty("address").GetString()));
        Assert.DoesNotContain("evil.example", File.ReadAllText(Path.Combine(host.DataRoot, "settings.json")));
        Assert.Equal(host.ConnectionString, host.Services.GetRequiredService<SettingsStore>().Current.ConnectionString);

        edited["defaultEngine"] = JsonSerializer.SerializeToElement("gemini");
        var bad = await client.PutAsJsonAsync("/api/settings", edited, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(3, host.Services.GetRequiredService<SettingsStore>().Current.MaxConcurrentJobs);

        // No mail password on this PC (DPAPI is Windows only), so the test fails with the reason instead of throwing.
        var test = await (await client.PostAsync("/api/settings/mail-test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(test.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(test.GetProperty("detail").GetString()));
    }
}
