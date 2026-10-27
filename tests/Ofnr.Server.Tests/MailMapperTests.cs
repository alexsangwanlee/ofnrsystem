using System.Text;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Mail;

namespace Ofnr.Server.Tests;

public sealed class MailMapperTests
{
    public static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures"));

    // Program.cs registers this at startup; the parser needs it before MailMapper ever runs.
    static MailMapperTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MimeMessage Load(string name) => MimeMessage.Load(Path.Combine(Fixtures, name));

    static MailMessage Map(MimeMessage m) => MailMapper.Map(m, "INBOX", 1, 1, outgoing: false);

    static MimeMessage WithoutMessageId(string subject)
    {
        var m = new MimeMessage { Subject = subject };
        m.Headers.Remove(HeaderId.MessageId);
        return m;
    }

    [Fact]
    public void Euc_kr_body_and_subject_decode()
    {
        var m = MailMapper.Map(Load("euckr.eml"), "INBOX", 7, 42, outgoing: false);

        Assert.Equal("견적 요청드립니다", m.Subject);
        Assert.Equal(("김담당", "kim@tnf.co.kr"), (m.FromName, m.FromAddress));
        Assert.Contains("면적은 30평입니다.", m.TextBody);
        Assert.Null(m.HtmlBody);
        Assert.Equal(("INBOX", 7L, 42L, false), (m.Folder, m.UidValidity, m.Uid, m.Outgoing));
        Assert.Equal(("euckr-1@tnf.co.kr", "euckr-1@tnf.co.kr"), (m.MessageId, m.ThreadKey));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 1, 15, 0, TimeSpan.Zero), m.ReceivedAt.ToUniversalTime());
        var a = Assert.Single(m.Attachments);
        Assert.Equal((0, "도면.pdf", 13L), (a.Index, a.FileName, a.Size));
    }

    [Fact]
    public void Html_is_sanitized_scripts_and_remote_images_removed()
    {
        var m = Map(Load("html.eml"));

        var html = m.HtmlBody!;
        foreach (var gone in new[] { "<script", "alert(1)", "<img", "track.example.com", "onclick", "style" }) Assert.DoesNotContain(gone, html);
        Assert.Contains("<b>대표님</b>", html);
        foreach (var kept in new[] { "href=\"https://ofnr.kr/x\"", "target=\"_blank\"", "rel=\"noopener\"" }) Assert.Contains(kept, html);
        // An HTML-only mail still gets text for search, triage and prompts.
        Assert.Contains("안녕하세요 대표님", m.TextBody);
        Assert.DoesNotContain("alert", m.TextBody);
    }

    [Fact]
    public void Thread_key_uses_references_root()
    {
        var m = Map(Load("reply.eml"));

        Assert.Equal("euckr-1@tnf.co.kr", m.ThreadKey);
        Assert.Equal("reply-1@ofnr.kr", m.InReplyTo);
        Assert.Equal(["euckr-1@tnf.co.kr", "reply-1@ofnr.kr"], m.References);

        var onlyInReplyTo = WithoutMessageId("RE: 견적");
        onlyInReplyTo.InReplyTo = "parent@tnf.co.kr";
        Assert.Equal("parent@tnf.co.kr", Map(onlyInReplyTo).ThreadKey);
    }

    [Fact]
    public void Subject_prefixes_removed_for_thread_fallback()
    {
        Assert.Equal("subj:견적", Map(WithoutMessageId("RE: 답장: 견적")).ThreadKey);
        Assert.Equal("subj:견적", Map(WithoutMessageId("Fwd: 전달:re:견적 ")).ThreadKey);
        Assert.Equal("subj:견적", Map(WithoutMessageId("FW: 견적")).ThreadKey);
    }

    [Fact]
    public void Match_by_job_number_beats_domain()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        (Guid, string, string[])[] projects = [(a, "OFNR-26001", ["tnf.co.kr"]), (b, "OFNR-26002", ["brand.co.kr"])];

        Assert.Equal(b, MailMapper.MatchProject(new MailMessage { FromAddress = "kim@TNF.co.kr", Subject = "[OFNR-26002] 시안 회신" }, projects));
        Assert.Equal(b, MailMapper.MatchProject(new MailMessage { FromAddress = "x@gmail.com", Subject = "회신", TextBody = "작업번호 ofnr-26002 건입니다" }, projects));
        Assert.Equal(a, MailMapper.MatchProject(new MailMessage { FromAddress = "kim@tnf.co.kr", Subject = "견적" }, projects));
        Assert.Null(MailMapper.MatchProject(new MailMessage { FromAddress = "x@gmail.com", Subject = "OFNR-26999 문의" }, projects));
        // A client with two active projects: the domain alone cannot tell which one.
        Assert.Null(MailMapper.MatchProject(new MailMessage { FromAddress = "kim@tnf.co.kr", Subject = "견적" }, [.. projects, (Guid.NewGuid(), "OFNR-26003", ["tnf.co.kr"])]));
    }
}
