using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Mail;

namespace Ofnr.Server.Tests;

public sealed class MailApiTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly FakeImap imap = new();
    readonly FakeSmtp smtp = new();

    JobHost Host() => new(s => s.AddSingleton<IImapSource>(imap).AddSingleton<ISmtpTransport>(smtp));

    static OfnrOptions Settings(JobHost h) => h.App.Services.GetRequiredService<SettingsStore>().Current;

    static async Task<MailMessage> AddMail(JobHost h, string subject, string? body = null, Action<MailMessage>? edit = null)
    {
        await using var db = h.Db();
        var m = new MailMessage
        {
            Folder = "INBOX", UidValidity = 1, Uid = Random.Shared.Next(1000, int.MaxValue), ThreadKey = "subj:" + subject,
            FromName = "김담당", FromAddress = "kim@tnf.co.kr", To = "ceo@ofnr.kr", Subject = subject, TextBody = body, ReceivedAt = DateTimeOffset.UtcNow,
        };
        edit?.Invoke(m);
        db.MailMessages.Add(m);
        await db.SaveChangesAsync(Ct);
        return m;
    }

    static string? Error(JsonElement e) => e.GetProperty("error").GetString();

    [Fact]
    public async Task Search_finds_two_syllable_korean_word()
    {
        await using var h = Host();
        await AddMail(h, "견적 요청드립니다");
        await AddMail(h, "시안 피드백", "수정 견적서도 같이 부탁드립니다");
        await AddMail(h, "계약서 송부");
        await AddMail(h, "100% 할인_행사");

        async Task<string?[]> Subjects(string query) =>
            [.. (await h.Send(HttpMethod.Get, "/api/mail" + query)).EnumerateArray().Select(m => m.GetProperty("subject").GetString()).Order(StringComparer.Ordinal)];

        Assert.Equal(["견적 요청드립니다", "시안 피드백"], await Subjects("?q=" + Uri.EscapeDataString("견적")));
        Assert.Equal(["100% 할인_행사"], await Subjects("?q=" + Uri.EscapeDataString("0% 할인_"))); // % and _ match only themselves
        Assert.Empty(await Subjects("?q=" + Uri.EscapeDataString("견_")));
        Assert.Equal(4, (await Subjects("?q=TNF.CO.KR")).Length); // the sender, ignoring case
        Assert.Single(await Subjects("?take=1"));
    }

    [Fact]
    public async Task Link_sets_status_linked()
    {
        await using var h = Host();
        var p = await h.Project();
        var mail = await AddMail(h, "견적 요청");

        var linked = await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/link", new { projectId = p.Id });

        Assert.Equal(("Linked", p.Id, p.JobNo), (linked.GetProperty("status").GetString(), linked.GetProperty("projectId").GetGuid(), linked.GetProperty("jobNo").GetString()));
        Assert.Contains(h.Notifier.Calls.OfType<MailSummaryDto>(), m => m.Id == mail.Id && m.Status == MailStatus.Linked);
        var project = await h.Send(HttpMethod.Get, $"/api/projects/{p.Id}");
        Assert.Equal(mail.Id, Assert.Single(project.GetProperty("mails").EnumerateArray()).GetProperty("id").GetGuid());
        var detail = await h.Send(HttpMethod.Get, $"/api/mail/{mail.Id}");
        Assert.Equal((p.JobNo, "ceo@ofnr.kr", 1), (detail.GetProperty("jobNo").GetString(), detail.GetProperty("to").GetString(), detail.GetProperty("thread").GetArrayLength()));
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("extracted").ValueKind);
        Assert.Equal(p.Id, (await h.Send(HttpMethod.Get, $"/api/mail?projectId={p.Id}&status=Linked")).EnumerateArray().Single().GetProperty("projectId").GetGuid());

        Assert.Equal("프로젝트를 찾을 수 없습니다.", Error(await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/link", new { projectId = Guid.NewGuid() }, HttpStatusCode.NotFound)));
        Assert.Equal("메일을 찾을 수 없습니다.", Error(await h.Send(HttpMethod.Post, $"/api/mail/{Guid.NewGuid()}/link", new { projectId = p.Id }, HttpStatusCode.NotFound)));
        Assert.Equal("Done", (await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/status", new { status = "Done" })).GetProperty("status").GetString());
        await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/status", new { status = "Nope" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_project_from_mail_links_mail()
    {
        await using var h = Host();
        var mail = await AddMail(h, "성수 팝업 견적 요청");

        var p = await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/create-project", new { name = "TNF 성수 팝업", venue = "성수" });

        Assert.True(Directory.Exists(Path.Combine(p.GetProperty("folderPath").GetString()!, "00_IN")));
        var detail = await h.Send(HttpMethod.Get, $"/api/mail/{mail.Id}");
        Assert.Equal((p.GetProperty("id").GetGuid(), "Linked"), (detail.GetProperty("projectId").GetGuid(), detail.GetProperty("status").GetString()));
        Assert.Equal("프로젝트 이름을 입력해 주세요.", Error(await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/create-project", new { name = " " }, HttpStatusCode.BadRequest)));
    }

    [Fact]
    public async Task Save_attachment_writes_into_00_IN()
    {
        await using var h = Host();
        var p = await h.Project();
        imap.Put("INBOX", 42, MailMapperTests.Load("euckr.eml"));
        var evil = new BodyBuilder { TextBody = "x" };
        ((MimePart)evil.Attachments.Add("a.txt", "a"u8.ToArray())).FileName = @"../..\악성.txt";
        imap.Put("INBOX", 43, new MimeMessage { Body = evil.ToMessageBody() });
        var mail = await AddMail(h, "견적 요청드립니다", edit: m => (m.Uid, m.ProjectId) = (42, p.Id));
        var traversal = await AddMail(h, "첨부", edit: m => (m.Uid, m.ProjectId) = (43, p.Id));

        var first = await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/attachments/0/save");
        var second = await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/attachments/0/save");

        Assert.Equal(("00_IN/도면.pdf", 13L), (first.GetProperty("relativePath").GetString(), first.GetProperty("size").GetInt64()));
        Assert.Equal("00_IN/도면_2.pdf", second.GetProperty("relativePath").GetString());
        Assert.Equal("%PDF-1.4 test", File.ReadAllText(Path.Combine(p.Folder, "00_IN", "도면.pdf")));
        Assert.Equal("00_IN/악성.txt", (await h.Send(HttpMethod.Post, $"/api/mail/{traversal.Id}/attachments/0/save")).GetProperty("relativePath").GetString());
        await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/attachments/1/save", expected: HttpStatusCode.NotFound);
        var unlinked = await AddMail(h, "미분류");
        Assert.Equal("먼저 메일을 프로젝트에 연결해 주세요.", Error(await h.Send(HttpMethod.Post, $"/api/mail/{unlinked.Id}/attachments/0/save", expected: HttpStatusCode.BadRequest)));
        var gone = await AddMail(h, "옮긴 메일", edit: m => (m.Uid, m.ProjectId) = (44, p.Id));
        Assert.Equal("이 메일이 메일함에 더 이상 없습니다. 웹메일에서 옮기거나 지웠는지 확인해 주세요.",
            Error(await h.Send(HttpMethod.Post, $"/api/mail/{gone.Id}/attachments/0/save", expected: HttpStatusCode.Conflict)));
        imap.UidValidity = 2;
        Assert.Equal("메일함이 바뀌어 첨부를 가져오지 못했습니다. 메일을 다시 동기화해 주세요.",
            Error(await h.Send(HttpMethod.Post, $"/api/mail/{mail.Id}/attachments/0/save", expected: HttpStatusCode.Conflict)));
    }

    [Fact]
    public async Task Send_builds_threading_headers()
    {
        await using var h = Host();
        Settings(h).Mail.Address = "ceo@ofnr.kr";
        var p = await h.Project();
        var quote = Path.Combine(p.Folder, "01_PLAN", $"{p.JobNo}_QUOTE_v01.pdf");
        File.WriteAllText(quote, "견적");
        var mail = await AddMail(h, "견적 요청", edit: m =>
        {
            (m.MessageId, m.References, m.ProjectId, m.Status) = ("m2@tnf.co.kr", ["root@tnf.co.kr", "m1@ofnr.kr"], p.Id, MailStatus.NeedsReply);
        });

        var draft = await h.Send(HttpMethod.Post, "/api/mail/drafts", new
        {
            replyToMailId = mail.Id, to = "김담당 <kim@tnf.co.kr>", cc = "pm@tnf.co.kr", subject = "RE: 견적 요청", body = "견적서를 보내 드립니다.", attachments = new[] { quote },
        });
        Assert.Equal((p.Id, "draft", "owner"), (draft.GetProperty("projectId").GetGuid(), draft.GetProperty("status").GetString(), draft.GetProperty("author").GetString()));
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal(id, (await h.Send(HttpMethod.Get, $"/api/mail/drafts?mailId={mail.Id}")).EnumerateArray().Single().GetProperty("id").GetGuid());

        var sent = await h.Send(HttpMethod.Post, $"/api/mail/drafts/{id}/send");

        Assert.Equal("sent", sent.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, sent.GetProperty("sentAt").ValueKind);
        var m = Assert.Single(smtp.Sent);
        Assert.Equal("m2@tnf.co.kr", m.InReplyTo);
        Assert.Equal(["root@tnf.co.kr", "m1@ofnr.kr", "m2@tnf.co.kr"], m.References);
        Assert.Equal("ceo@ofnr.kr", m.From.Mailboxes.Single().Address);
        Assert.Equal(("kim@tnf.co.kr", "pm@tnf.co.kr"), (m.To.Mailboxes.Single().Address, m.Cc.Mailboxes.Single().Address));
        Assert.Equal(("RE: 견적 요청", "견적서를 보내 드립니다."), (m.Subject, m.TextBody?.Trim()));
        Assert.Equal($"{p.JobNo}_QUOTE_v01.pdf", Assert.Single(m.Attachments).ContentDisposition?.FileName);
        Assert.Empty(imap.Appended);
        await using (var db = h.Db())
        {
            Assert.Equal(MailStatus.Done, (await db.MailMessages.SingleAsync(x => x.Id == mail.Id, Ct)).Status);
            Assert.Contains(await db.Activities.Where(a => a.ProjectId == p.Id).Select(a => a.Text).ToListAsync(Ct), t => t == "메일을 보냈습니다: RE: 견적 요청");
        }
        Assert.Equal("이미 보낸 메일입니다.", Error(await h.Send(HttpMethod.Post, $"/api/mail/drafts/{id}/send", expected: HttpStatusCode.Conflict)));
        await h.Send(HttpMethod.Put, $"/api/mail/drafts/{id}", new { to = "x@y.kr", subject = "s", body = "b", attachments = Array.Empty<string>() }, HttpStatusCode.Conflict);

        // With the setting on, a copy goes to the Sent folder, read.
        Settings(h).AppendSentCopy = true;
        var second = (await h.Send(HttpMethod.Post, "/api/mail/drafts", new { to = "kim@tnf.co.kr", subject = "추가 자료", body = "보내 드립니다.", attachments = Array.Empty<string>() }))
            .GetProperty("id").GetGuid();
        await h.Send(HttpMethod.Post, $"/api/mail/drafts/{second}/send");
        Assert.Equal("보낸메일함", Assert.Single(imap.Appended).Folder);
    }

    [Fact]
    public async Task Send_rejects_attachment_outside_projects_root()
    {
        await using var h = Host();
        Settings(h).Mail.Address = "ceo@ofnr.kr";
        var p = await h.Project();
        var outside = Path.Combine(h.DataRoot, "settings.json");
        var climbing = Path.Combine(p.Folder, "..", "..", "..", "settings.json");

        foreach (var path in new[] { outside, climbing })
        {
            var id = (await h.Send(HttpMethod.Post, "/api/mail/drafts", new { to = "kim@tnf.co.kr", subject = "자료", body = "첨부합니다.", attachments = new[] { path } }))
                .GetProperty("id").GetGuid();

            var e = await h.Send(HttpMethod.Post, $"/api/mail/drafts/{id}/send", expected: HttpStatusCode.BadRequest);

            Assert.Equal($"프로젝트 폴더 안의 파일만 첨부할 수 있습니다: {path}", Error(e));
            await using var db = h.Db();
            Assert.Equal("draft", (await db.MailDrafts.SingleAsync(d => d.Id == id, Ct)).Status);
        }
        Assert.Empty(smtp.Sent);
        Assert.Equal("받는 사람을 입력해 주세요.", Error(await h.Send(HttpMethod.Post, "/api/mail/drafts", new { to = " ", subject = "s", body = "b", attachments = Array.Empty<string>() }, HttpStatusCode.BadRequest)));
    }
}
