using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Security;

namespace Ofnr.Server.Tests;

public sealed class DataTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Project NewProject(string jobNo) => new() { JobNo = jobNo, Name = "성수 팝업", Slug = "seongsu", FolderPath = "/p/" + jobNo };

    static MailMessage NewMail(string folder, long uidValidity, long uid) => new()
    {
        Folder = folder, UidValidity = uidValidity, Uid = uid, Subject = "견적 요청드립니다", ReceivedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Migrations_apply_on_empty_database()
    {
        using var t = new TestDb();
        await using var db = t.Context();
        await db.Database.MigrateAsync(Ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Ct), m => m.EndsWith("_Initial"));
        var snakeColumns = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'projects' AND column_name = 'job_no'")
            .SingleAsync(Ct);
        Assert.Equal(1, snakeColumns);
    }

    [Fact]
    public async Task JobNo_is_unique()
    {
        using var t = TestDb.Create();
        await using var db = t.Context();
        db.Projects.Add(NewProject("OFNR-26001"));
        await db.SaveChangesAsync(Ct);

        await using var db2 = t.Context();
        db2.Projects.Add(NewProject("OFNR-26001"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Mail_uid_is_unique_per_folder_and_validity()
    {
        using var t = TestDb.Create();
        await using var db = t.Context();
        db.MailMessages.AddRange(NewMail("INBOX", 1, 100), NewMail("INBOX", 2, 100), NewMail("보낸메일함", 1, 100));
        await db.SaveChangesAsync(Ct);

        await using var db2 = t.Context();
        db2.MailMessages.Add(NewMail("INBOX", 1, 100));
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Client_mail_domains_roundtrip()
    {
        using var t = TestDb.Create();
        var client = new Client { Name = "노스페이스", MailDomains = ["tnf.co.kr", "naver.com"] };
        await using (var db = t.Context())
        {
            db.Clients.Add(client);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = t.Context();
        var loaded = await read.Clients.SingleAsync(c => c.Id == client.Id, Ct);
        Assert.Equal(["tnf.co.kr", "naver.com"], loaded.MailDomains);
        Assert.True(await read.Clients.AnyAsync(c => c.MailDomains.Contains("naver.com"), Ct));
    }

    [Fact]
    public async Task Jsonb_extracted_roundtrip_with_korean()
    {
        using var t = TestDb.Create();
        var mail = NewMail("INBOX", 1, 1);
        mail.Extracted = JsonDocument.Parse("""{"brand":"노스페이스"}""");
        mail.Attachments = [new() { Index = 0, FileName = "견적서.xlsx", Size = 1234 }];
        var job = new Job { RecipeId = "quote", Title = "견적서", Instruction = "성수 팝업 견적서", Result = new() { Summary = "견적서를 만들었습니다", Files = ["01_PLAN/OFNR-26001_QUOTE_v01.xlsx"] } };
        await using (var db = t.Context())
        {
            db.MailMessages.Add(mail);
            db.Jobs.Add(job);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = t.Context();
        var loaded = await read.MailMessages.SingleAsync(m => m.Id == mail.Id, Ct);
        Assert.Equal("노스페이스", loaded.Extracted!.RootElement.GetProperty("brand").GetString());
        Assert.Equal("견적서.xlsx", Assert.Single(loaded.Attachments).FileName);
        var stored = await read.Database.SqlQuery<string>($"SELECT extracted ->> 'brand' AS \"Value\" FROM mail_messages").SingleAsync(Ct);
        Assert.Equal("노스페이스", stored);
        var result = (await read.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Result!;
        Assert.Equal("견적서를 만들었습니다", result.Summary);
        Assert.Equal(["01_PLAN/OFNR-26001_QUOTE_v01.xlsx"], result.Files);
    }

    [Fact]
    public async Task Kst_offsets_are_stored_as_utc()
    {
        using var t = TestDb.Create();
        var kst = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(9));
        var mail = NewMail("INBOX", 1, 1);
        mail.ReceivedAt = kst;
        await using (var db = t.Context())
        {
            db.AddRange(mail, new MailDraft { Subject = "회신", SentAt = kst });
            await db.SaveChangesAsync(Ct);
        }

        await using var read = t.Context();
        Assert.Equal(kst, (await read.MailMessages.SingleAsync(m => m.ReceivedAt == kst, Ct)).ReceivedAt);
        Assert.Equal(kst, (await read.MailDrafts.SingleAsync(d => d.SentAt == kst, Ct)).SentAt);
    }

    [Fact]
    public async Task Server_serves_health_when_postgres_is_down()
    {
        await using var host = new TestHost();
        var down = host.WithWebHostBuilder(b => b.UseSetting("OFNR_DB", "Host=127.0.0.1;Port=1;Database=ofnr_down;Username=ofnr;Password=x"));
        var client = down.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ofnr-Token", LocalAccess.Token);

        var db = (await client.GetFromJsonAsync<JsonElement>("/api/health", Ct)).GetProperty("db");

        Assert.False(db.GetProperty("ok").GetBoolean());
        Assert.StartsWith("데이터베이스에 연결하지 못했습니다: ", db.GetProperty("detail").GetString());
    }
}
