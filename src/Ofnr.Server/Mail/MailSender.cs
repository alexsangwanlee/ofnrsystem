using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Projects;
using Ofnr.Server.Realtime;
using Activity = Ofnr.Server.Data.Activity;

namespace Ofnr.Server.Mail;

public interface ISmtpTransport
{
    Task SendAsync(MimeMessage m);
}

/// <summary>The NAVER WORKS account over SMTP (SSL on 465, STARTTLS elsewhere).</summary>
public sealed class MailKitSmtp(SettingsStore settings, SecretStore secrets) : ISmtpTransport
{
    public async Task SendAsync(MimeMessage m)
    {
        var a = settings.Current.Mail;
        var password = secrets.Load("mail") ?? throw new MailException("메일 비밀번호가 설정되지 않았습니다.");
        using var client = new SmtpClient();
        await client.ConnectAsync(a.SmtpHost, a.SmtpPort, a.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(a.Address, password);
        await client.SendAsync(m);
        await client.DisconnectAsync(true);
    }
}

/// <summary>The only code that sends mail; POST /api/mail/drafts/{id}/send calls it when the CEO presses send.</summary>
public sealed class MailSender(OfnrDb db, SettingsStore settings, ISmtpTransport smtp, IImapSource imap, IOpsNotifier notifier, ILogger<MailSender> logger)
{
    /// <summary>Sends the tracked draft and marks it sent. Anything the CEO must fix throws <see cref="MailException"/> before the mail goes out.</summary>
    public async Task SendAsync(MailDraft d)
    {
        var o = settings.Current;
        if (o.Mail.Address is not { Length: > 0 }) throw new MailException(MailSync.NotConfigured);
        var m = new MimeMessage { Subject = d.Subject };
        try
        {
            m.From.Add(MailboxAddress.Parse(o.Mail.Address));
            m.To.AddRange(InternetAddressList.Parse(d.To));
            if (!string.IsNullOrWhiteSpace(d.Cc)) m.Cc.AddRange(InternetAddressList.Parse(d.Cc));
        }
        catch (ParseException) { throw new MailException("메일 주소를 확인해 주세요."); }
        var replied = d.ReplyToMailId is { } id ? await db.MailMessages.FindAsync(id) : null;
        if (replied?.MessageId is { } parent)
        {
            m.InReplyTo = parent;
            foreach (var r in replied.References) m.References.Add(r);
            m.References.Add(parent);
        }
        var body = new BodyBuilder { TextBody = d.Body };
        foreach (var path in d.Attachments)
        {
            if (!Path.IsPathFullyQualified(path) || !ProjectEndpoints.IsUnder(Path.GetFullPath(path), o.ProjectsRoot))
                throw new MailException($"프로젝트 폴더 안의 파일만 첨부할 수 있습니다: {path}");
            if (!File.Exists(path)) throw new MailException($"첨부 파일을 찾을 수 없습니다: {path}");
            await body.Attachments.AddAsync(path);
        }
        m.Body = body.ToMessageBody();

        try { await smtp.SendAsync(m); }
        catch (Exception e) when (e is not MailException) { throw new MailException($"메일을 보내지 못했습니다: {e.Message}", e); }

        if (o.AppendSentCopy)
        {
            // The mail is already out: a failed copy must not fail the send, or a retry would reach the client twice.
            try
            {
                if (await MailSync.SentFolderAsync(imap, o.Mail) is { } sent) await imap.AppendSeenAsync(sent, m);
            }
            catch (Exception e) { logger.LogWarning(e, "Copying the sent mail to the Sent folder failed"); }
        }
        var now = DateTimeOffset.UtcNow;
        (d.Status, d.SentAt, d.UpdatedAt) = ("sent", now, now);
        if (replied is not null) replied.Status = MailStatus.Done;
        db.Activities.Add(new Activity { ProjectId = d.ProjectId ?? replied?.ProjectId, Kind = "mail", Text = $"메일을 보냈습니다: {d.Subject}" });
        await db.SaveChangesAsync();
        if (replied is not null) await notifier.MailUpdated(await MailEndpoints.SummaryAsync(db, replied.Id));
    }
}
