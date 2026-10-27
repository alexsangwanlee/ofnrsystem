using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Ofnr.Server.Data;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class ProjectApiTests(TestHost host) : IClassFixture<TestHost>
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string ProjectsRoot => Path.Combine(host.DataRoot, "Projects");

    async Task<JsonElement> Send(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await host.Authed().SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }, Ct);
        Assert.Equal(expected, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    Task<JsonElement> CreateProject(object body) => Send(HttpMethod.Post, "/api/projects", body);

    async Task AddArtifact(JsonElement project, string relativePath, string docCode, ArtifactStatus status = ArtifactStatus.Draft)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
        db.Artifacts.Add(new Artifact
        {
            ProjectId = project.GetProperty("id").GetGuid(), RelativePath = relativePath, DocCode = docCode, Version = 1, Status = status,
            ModifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Create_project_creates_folder_and_returns_jobNo()
    {
        var client = await Send(HttpMethod.Post, "/api/clients", new { name = "노스페이스", mailDomains = new[] { "TNF.co.kr " } });
        Assert.Equal(["tnf.co.kr"], client.GetProperty("mailDomains").EnumerateArray().Select(d => d.GetString()));

        var p = await CreateProject(new { name = "TNF 성수 팝업", clientId = client.GetProperty("id").GetGuid(), venue = "성수", openDate = "2026-11-20", quoteAmount = 12_500_000 });

        var year = DateTimeOffset.Now.Year;
        var jobNo = p.GetProperty("jobNo").GetString()!;
        Assert.Matches($"^OFNR-{year % 100:D2}\\d{{3}}$", jobNo);
        Assert.Equal("TNF", p.GetProperty("slug").GetString());
        Assert.Equal("노스페이스", p.GetProperty("clientName").GetString());
        Assert.Equal("Inquiry", p.GetProperty("stage").GetString());
        Assert.Equal("2026-11-20", p.GetProperty("openDate").GetString());
        Assert.Equal(12_500_000, p.GetProperty("quoteAmount").GetInt64());
        var folder = p.GetProperty("folderPath").GetString()!;
        Assert.Equal(Path.Combine(ProjectsRoot, year.ToString(), jobNo + "_TNF"), folder);
        Assert.True(Directory.Exists(Path.Combine(folder, "00_IN")));

        var list = await Send(HttpMethod.Get, "/api/projects?stage=Inquiry");
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("jobNo").GetString() == jobNo);
        var detail = await Send(HttpMethod.Get, $"/api/projects/{p.GetProperty("id")}");
        Assert.Equal(jobNo, detail.GetProperty("project").GetProperty("jobNo").GetString());
        Assert.Single(detail.GetProperty("activity").EnumerateArray());
        Assert.Empty(detail.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Create_project_without_name_is_400()
    {
        var res = await Send(HttpMethod.Post, "/api/projects", new { name = " " }, HttpStatusCode.BadRequest);
        Assert.Equal("프로젝트 이름을 입력해 주세요.", res.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Concurrent_creates_get_distinct_numbers_and_one_folder_each()
    {
        var yearDir = Path.Combine(ProjectsRoot, DateTimeOffset.Now.Year.ToString());
        Directory.CreateDirectory(yearDir);
        var before = Directory.GetDirectories(yearDir);

        var created = await Task.WhenAll(Enumerable.Range(1, 8).Select(n => CreateProject(new { name = $"Popup {n}" })));

        var jobNos = created.Select(p => p.GetProperty("jobNo").GetString()).ToList();
        Assert.Equal(8, jobNos.Distinct().Count());
        Assert.Equal(8, Directory.GetDirectories(yearDir).Except(before).Count());
    }

    [Fact]
    public async Task Bad_request_shapes_return_korean_error()
    {
        var p = await CreateProject(new { name = "형식 팝업" });
        var stage = $"/api/projects/{p.GetProperty("id")}/stage";
        const string shape = "요청 형식이 올바르지 않습니다.";
        var bad = new (HttpMethod method, string path, string? json, string error)[]
        {
            (HttpMethod.Post, "/api/projects", """{"name":"테스트 팝업","stage":99}""", shape),
            (HttpMethod.Post, "/api/projects", """{"name":"테스트 팝업","stage":"99"}""", shape),
            (HttpMethod.Post, "/api/projects", """{"name":"테스트 팝업","stage":"Report, Done"}""", "알 수 없는 단계입니다."),
            (HttpMethod.Post, "/api/projects", """{"name":""", shape),
            (HttpMethod.Post, stage, """{"stage":-1}""", shape),
            (HttpMethod.Post, stage, """{"stage":"Nope"}""", shape),
            (HttpMethod.Post, stage, """{"stage":"Report, Done"}""", "옮길 단계를 선택해 주세요."),
            (HttpMethod.Get, "/api/projects?stage=Foo", null, shape),
        };

        foreach (var (method, path, json, error) in bad)
        {
            var res = await host.Authed().SendAsync(
                new HttpRequestMessage(method, path) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") }, Ct);
            var text = await res.Content.ReadAsStringAsync(Ct);
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{method} {path} {json}: {(int)res.StatusCode} {text}");
            Assert.Equal(error, JsonDocument.Parse(text).RootElement.GetProperty("error").GetString());
        }
        Assert.Equal("Inquiry", (await Send(HttpMethod.Get, $"/api/projects/{p.GetProperty("id")}")).GetProperty("project").GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Bad_request_in_production_also_returns_korean_error()
    {
        // Production does not throw on binding failures by default; Development (the other tests) does.
        await using var prod = host.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = prod.CreateClient(new() { BaseAddress = new Uri("http://127.0.0.1:47821") });
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);

        var res = await client.GetAsync("/api/projects?stage=Foo", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("요청 형식이 올바르지 않습니다.", (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_project_keeps_folder_and_logs_activity()
    {
        var p = await CreateProject(new { name = "더현대 팝업", instructions = "주말 설치 금지" });
        var id = p.GetProperty("id").GetGuid();
        // The web edit form sends back the instructions it received; without them in the DTO every edit wiped them.
        Assert.Equal("주말 설치 금지", p.GetProperty("instructions").GetString());

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
            var job = new Job { ProjectId = id, RecipeId = "quote", Title = "견적서", Status = JobStatus.Review, Result = new() { Summary = "견적서를 만들었습니다" } };
            db.AddRange(job, new JobRun { JobId = job.Id, Seq = 1, CostUsd = 0.42m });
            db.MailMessages.Add(new MailMessage
            {
                Folder = "INBOX", Uid = 1, ProjectId = id, Subject = "견적 요청", ReceivedAt = DateTimeOffset.UtcNow, Attachments = [new() { FileName = "도면.pdf" }],
            });
            await db.SaveChangesAsync(Ct);
        }

        var detail = await Send(HttpMethod.Put, $"/api/projects/{id}", new { name = "더현대 서울 팝업", slug = "Other", venue = "여의도", contractAmount = 30_000_000, instructions = "철수는 오전 9시 이후" });

        var updated = detail.GetProperty("project");
        Assert.Equal("더현대 서울 팝업", updated.GetProperty("name").GetString());
        Assert.Equal("철수는 오전 9시 이후", updated.GetProperty("instructions").GetString());
        Assert.Equal(p.GetProperty("folderPath").GetString(), updated.GetProperty("folderPath").GetString());
        Assert.Equal(30_000_000, updated.GetProperty("contractAmount").GetInt64());
        Assert.Equal(2, detail.GetProperty("activity").GetArrayLength());
        Assert.Equal(1, updated.GetProperty("attentionCount").GetInt32());
        var jobDto = Assert.Single(detail.GetProperty("jobs").EnumerateArray());
        Assert.Equal("견적서를 만들었습니다", jobDto.GetProperty("summary").GetString());
        Assert.Equal(0.42m, jobDto.GetProperty("costUsd").GetDecimal());
        Assert.Equal("Review", jobDto.GetProperty("status").GetString());
        Assert.True(Assert.Single(detail.GetProperty("mails").EnumerateArray()).GetProperty("hasAttachments").GetBoolean());
        await Send(HttpMethod.Put, $"/api/projects/{Guid.NewGuid()}", new { name = "x" }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Clients_update_and_list()
    {
        var c = await Send(HttpMethod.Post, "/api/clients", new { name = "삼성전자", mailDomains = Array.Empty<string>() });
        var id = c.GetProperty("id").GetGuid();

        await Send(HttpMethod.Put, $"/api/clients/{id}", new { name = "삼성전자 MX", contactName = "김담당", mailDomains = new[] { "samsung.com" } });

        var all = await Send(HttpMethod.Get, "/api/clients");
        var row = all.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("삼성전자 MX", row.GetProperty("name").GetString());
        Assert.Equal("김담당", row.GetProperty("contactName").GetString());
        await Send(HttpMethod.Put, $"/api/clients/{Guid.NewGuid()}", new { name = "x", mailDomains = Array.Empty<string>() }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stage_move_returns_suggestions_and_warning()
    {
        var p = await CreateProject(new { name = "성수 팝업" });
        var id = p.GetProperty("id");

        var planning = await Send(HttpMethod.Post, $"/api/projects/{id}/stage", new { stage = "Planning" });
        Assert.Equal(["proposal", "quote", "document"], planning.GetProperty("suggestions").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Empty(planning.GetProperty("warnings").EnumerateArray());

        var design = await Send(HttpMethod.Post, $"/api/projects/{id}/stage", new { stage = "Design" });
        Assert.Equal("Design", design.GetProperty("project").GetProperty("stage").GetString());
        Assert.Equal(["model3d", "graphic"], design.GetProperty("suggestions").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(["견적서나 제안서 없이 디자인 단계로 넘어갑니다."], design.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        var quoted = await CreateProject(new { name = "여의도 팝업", stage = "Planning" });
        await AddArtifact(quoted, "01_PLAN/x_QUOTE_v01.xlsx", "QUOTE");
        var gated = await Send(HttpMethod.Post, $"/api/projects/{quoted.GetProperty("id")}/stage", new { stage = "Design" });
        Assert.Empty(gated.GetProperty("warnings").EnumerateArray());

        // Dragging a card past design still crosses the contract gate.
        var jumped = await CreateProject(new { name = "건너뛰기 팝업" });
        var skip = await Send(HttpMethod.Post, $"/api/projects/{jumped.GetProperty("id")}/stage", new { stage = "Production" });
        Assert.Equal(["견적서나 제안서 없이 디자인 단계로 넘어갑니다."], skip.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        var past = await Send(HttpMethod.Post, $"/api/projects/{jumped.GetProperty("id")}/stage", new { stage = "Operation" });
        Assert.Empty(past.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Files_list_folder_joined_with_artifacts()
    {
        var p = await CreateProject(new { name = "파일 팝업" });
        var folder = p.GetProperty("folderPath").GetString()!;
        var quote = $"01_PLAN/{p.GetProperty("jobNo").GetString()}_QUOTE_v01.xlsx";
        File.WriteAllText(Path.Combine(folder, quote), "q");
        File.WriteAllText(Path.Combine(folder, "00_IN", "도면.pdf"), "pdf");
        await AddArtifact(p, quote, "QUOTE", ArtifactStatus.InternalApproved);

        var files = (await Send(HttpMethod.Get, $"/api/projects/{p.GetProperty("id")}/files")).EnumerateArray().ToList();

        Assert.Equal(["00_IN/도면.pdf", quote], files.Select(f => f.GetProperty("relativePath").GetString()));
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("artifactId").ValueKind);
        Assert.Equal("InternalApproved", files[1].GetProperty("status").GetString());
        Assert.Equal(1, files[1].GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Open_rejects_exe_and_paths_outside_roots()
    {
        await Send(HttpMethod.Post, "/api/open", new { path = Path.Combine(ProjectsRoot, "x", "run.exe") }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/open", new { path = "/etc/passwd" }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/open", new { path = Path.Combine(ProjectsRoot, "..", "x.pdf") }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/open", new { path = ProjectsRoot + "-evil/x.pdf" }, HttpStatusCode.BadRequest);

        var p = await CreateProject(new { name = "열기 팝업" });
        var folder = p.GetProperty("folderPath").GetString()!;
        await Send(HttpMethod.Post, "/api/open", new { path = folder }, HttpStatusCode.NoContent);
        await Send(HttpMethod.Post, "/api/open", new { path = Path.Combine(folder, "00_IN", "없음.pdf") }, HttpStatusCode.NotFound);
    }
}
