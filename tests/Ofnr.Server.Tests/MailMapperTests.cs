using System.Text;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Mail;

namespace Ofnr.Server.Tests;

public sealed class MailMapperTests
{
    public static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures"));

    // Program.cs calls this at startup; the parser needs it before MailMapper ever runs.
    static MailMapperTests() => MailMapper.RegisterKoreanEncodings();

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
    public void Nul_characters_are_dropped_before_postgres_sees_them()
    {
        var m = Map(MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes("""
            From: =?utf-8?Q?=EA=B9=80=00?= <kim@tnf.co.kr>
            Subject: =?utf-8?Q?hi=00x?=
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain; charset=utf-8
            Content-Transfer-Encoding: base64

            6rKs7KCBAOyalOyyrQ==
            --b
            Content-Type: application/pdf
            Content-Disposition: attachment; filename*=utf-8''a%00b.pdf

            %PDF
            --b--
            """)), TestContext.Current.CancellationToken));

        Assert.Equal(("hix", "견적요청", "김"), (m.Subject, m.TextBody!.Trim(), m.FromName));
        Assert.Equal("ab.pdf", Assert.Single(m.Attachments).FileName);
        Assert.Equal("subj:hix", m.ThreadKey);
    }

    [Fact]
    public void Raw_8bit_korean_headers_and_charsetless_body_decode()
    {
        // Legacy Korean mailers send CP949 bytes without RFC 2047 encoding or a charset parameter.
        var m = Map(MimeMessage.Load(new MemoryStream(Encoding.GetEncoding(949).GetBytes("""
            From: kim@tnf.co.kr
            Subject: 견적 요청드립니다
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            본문 견적
            --b
            Content-Type: application/pdf; name="도면.pdf"
            Content-Disposition: attachment; filename="도면.pdf"

            %PDF
            --b--
            """)), TestContext.Current.CancellationToken));

        Assert.Equal("견적 요청드립니다", m.Subject);
        Assert.Equal("본문 견적", m.TextBody?.Trim());
        Assert.Equal("도면.pdf", Assert.Single(m.Attachments).FileName);
    }

    [Fact]
    public void Inline_parts_with_a_file_name_are_attachments()
    {
        // Apple Mail sends attached files inline; some mailers name a part only in Content-Type.
        var m = Map(MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes("""
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain; charset=utf-8

            plan attached
            --b
            Content-Type: application/pdf; name="plan.pdf"
            Content-Disposition: inline; filename="plan.pdf"

            %PDF
            --b
            Content-Type: image/jpeg; name="site.jpg"

            jpg
            --b--
            """)), TestContext.Current.CancellationToken));

        Assert.Equal(["plan.pdf", "site.jpg"], m.Attachments.Select(a => a.FileName));
        Assert.Equal("plan attached", m.TextBody?.Trim());
    }

    [Fact]
    public void Deeply_nested_html_neither_overflows_the_stack_nor_stalls()
    {
        static MimeMessage Nested(int depth) => new() { Body = new TextPart("html") { Text = string.Concat(Enumerable.Repeat("<div>", depth)) + "안녕하세요" } };
        MailMessage? deep = null, huge = null;
        Exception? error = null;
        // 1 MB, the default stack of a .NET thread on Windows; a stack overflow would end the whole server.
        var t = new Thread(() =>
        {
            try { (deep, huge) = (Map(Nested(5000)), Map(Nested(30000))); }
            catch (Exception e) { error = e; }
        }, 1024 * 1024);
        t.Start();
        t.Join();

        Assert.Null(error);
        Assert.Equal("안녕하세요", deep!.TextBody?.Trim());
        Assert.NotNull(deep.HtmlBody);
        // Parsing nested tags takes quadratic time, so very large HTML is not shown.
        Assert.Null(huge!.HtmlBody);
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
