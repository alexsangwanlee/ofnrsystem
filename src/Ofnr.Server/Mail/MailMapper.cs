using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Ganss.Xss;
using MimeKit;
using MimeKit.IO;
using Ofnr.Server.Data;

namespace Ofnr.Server.Mail;

public static partial class MailMapper
{
    [GeneratedRegex(@"^(?:\s*(?:re|fwd?|답장|전달)\s*:)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefixes();

    [GeneratedRegex(@"OFNR-\d{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JobNumber();

    // shortcut: HTML with more tags is not kept, since parsing nested tags takes quadratic time; raise it if real mail hits it.
    const int MaxHtmlTags = 20_000;

    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Called once at startup, before any mail is parsed: EUC-KR and ks_c_5601-1987 parts decode, and raw 8-bit headers that are not UTF-8 (legacy Korean mailers) read as CP949.</summary>
    public static void RegisterKoreanEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ParserOptions.Default.CharsetEncoding = Encoding.GetEncoding(949);
    }

    public static MailMessage Map(MimeMessage m, string folder, uint uidValidity, uint uid, bool outgoing)
    {
        // A text part without a charset that is not UTF-8 is legacy Korean, not the Latin-1 MimeKit would fall back to.
        foreach (var t in m.BodyParts.OfType<TextPart>())
            if (!t.TryDetectEncoding(out _, out _) && !IsUtf8(t)) t.ContentType.CharsetEncoding = Encoding.GetEncoding(949);
        var from = m.From.Mailboxes.FirstOrDefault();
        string[] references = [.. m.References];
        var subject = m.Subject ?? "";
        using var html = m.HtmlBody is { } raw && raw.Count(c => c == '<') <= MaxHtmlTags ? Sanitize(raw) : null;
        return new MailMessage
        {
            Folder = folder,
            UidValidity = uidValidity,
            Uid = uid,
            Outgoing = outgoing,
            MessageId = m.MessageId,
            InReplyTo = m.InReplyTo,
            References = references,
            ThreadKey = references.FirstOrDefault() ?? m.InReplyTo ?? m.MessageId ?? "subj:" + ReplyPrefixes().Replace(subject, "").Trim(),
            FromName = from?.Name ?? "",
            FromAddress = from?.Address ?? "",
            To = m.To.ToString(),
            Cc = m.Cc.Count > 0 ? m.Cc.ToString() : null,
            Subject = subject,
            // Without a Date header MimeKit gives MinValue, which would sort the mail below every other.
            ReceivedAt = m.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : m.Date,
            // HTML-only mail (common from Korean webmail) still needs text for search, triage and prompts.
            TextBody = m.TextBody ?? (html?.Body is { } body ? Text(body) : null),
            HtmlBody = html?.Body?.InnerHtml,
            Attachments = [.. Attachments(m).Select((a, i) => new MailAttachment { Index = i, FileName = FileName(a), Size = Size(a) })],
        };
    }

    /// <summary>The job number in the subject or body first, then a sender domain that only one project's client uses.</summary>
    public static Guid? MatchProject(MailMessage mail, IEnumerable<(Guid id, string jobNo, string[] domains)> projects)
    {
        var list = projects.ToList();
        foreach (Match number in JobNumber().Matches($"{mail.Subject}\n{mail.TextBody}"))
            foreach (var p in list)
                if (p.jobNo.Equals(number.Value, StringComparison.OrdinalIgnoreCase)) return p.id;
        var domain = mail.FromAddress.Split('@') is [_, var d] ? d.ToLowerInvariant() : null;
        var byDomain = list.Where(p => domain is not null && p.domains.Contains(domain)).Select(p => p.id).Distinct().ToList();
        return byDomain.Count == 1 ? byDomain[0] : null;
    }

    /// <summary>
    /// The attachments in the order <see cref="MailAttachment.Index"/> numbers them: parts marked attachment, and parts with a file name,
    /// since Apple Mail sends attached files inline and some mailers name a part only in Content-Type.
    /// </summary>
    public static IReadOnlyList<MimeEntity> Attachments(MimeMessage m) => [.. m.BodyParts.Where(p => p.IsAttachment || p is MimePart { FileName: not null })];

    public static string FileName(MimeEntity a) => a.ContentDisposition?.FileName ?? a.ContentType.Name ?? (a is MessagePart ? "첨부 메일.eml" : "첨부 파일");

    /// <summary>Writes the decoded attachment.</summary>
    public static void Decode(MimeEntity a, Stream to)
    {
        if (a is MessagePart mp) mp.Message?.WriteTo(to);
        else if (a is MimePart p) p.Content?.DecodeTo(to);
    }

    static long Size(MimeEntity a)
    {
        using var s = new MeasuringStream();
        Decode(a, s);
        return s.Length;
    }

    static bool IsUtf8(TextPart t)
    {
        try { t.GetText(StrictUtf8); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    // GetInnerText, TextContent and the sanitizer's PostProcessNode (Node.Normalize) recurse per element: deeply nested mail would
    // overflow the stack and end the server. GetDescendants walks without recursion.
    static string Text(IElement body) => string.Concat(body.GetDescendants().Select(n => n switch
    {
        IText t => t.Data,
        IElement { LocalName: "br" or "p" or "div" or "li" or "tr" or "blockquote" } => "\n",
        _ => "",
    })).Trim();

    static IHtmlDocument Sanitize(string html)
    {
        var s = new HtmlSanitizer();
        // Remote images are tracking pixels; without src no element loads anything. Inline style goes too.
        s.AllowedTags.Remove("img");
        s.AllowedAttributes.Remove("src");
        s.AllowedAttributes.Remove("style");
        var doc = s.SanitizeDom(html);
        foreach (var a in doc.GetDescendants().OfType<IHtmlAnchorElement>())
        {
            a.SetAttribute("target", "_blank");
            a.SetAttribute("rel", "noopener");
        }
        return doc;
    }
}
