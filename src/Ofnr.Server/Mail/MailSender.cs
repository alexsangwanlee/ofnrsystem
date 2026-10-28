using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Utils;
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
        try { await client.AuthenticateAsync(a.Address, password); }
        catch (AuthenticationException e) { throw new MailException(MailSync.LoginFailed, e); }
        await client.SendAsync(m);
        // The mail is out; a failed QUIT must not report a failed send, which a retry would turn into a second mail.
        try { await client.DisconnectAsync(true); }
        catch (Exception) { }
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
            var from = MailboxAddress.Parse(o.Mail.Address);
            m.From.Add(from);
            m.MessageId = MimeUtils.GenerateMessageId(from.Domain); // not the PC's host name
            // Outlook and NAVER WORKS separate recipients with ';', which MimeKit does not.
            m.To.AddRange(InternetAddressList.Parse(d.To.Replace(';', ',')));
            if (!string.IsNullOrWhiteSpace(d.Cc)) m.Cc.AddRange(InternetAddressList.Parse(d.Cc.Replace(';', ',')));
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
            MimeEntity part;
            try
            {
                // Office keeps an open file locked for writing; sharing it sends the last saved version.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                part = await body.Attachments.AddAsync(Path.GetFileName(path), file);
            }
            catch (IOException) { throw new MailException($"첨부 파일을 열지 못했습니다. 파일을 연 프로그램을 닫고 다시 보내 주세요: {path}"); }
            // Older Outlook shows RFC 2231 names, MimeKit's default, as ATT00001; RFC 2047 reads everywhere.
            foreach (var p in part.ContentType.Parameters.Concat(part.ContentDisposition?.Parameters ?? [])) p.EncodingMethod = ParameterEncodingMethod.Rfc2047;
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
        // Project files that went out are now with the client.
        if (d.ProjectId is { } projectId && d.Attachments.Length > 0 && await db.Projects.FindAsync(projectId) is { } project)
        {
            var sentFiles = d.Attachments.Select(f => Path.GetRelativePath(project.FolderPath, f).Replace('\\', '/')).ToList();
            foreach (var a in await db.Artifacts.Where(a => a.ProjectId == projectId && sentFiles.Contains(a.RelativePath)
                && (a.Status == ArtifactStatus.Draft || a.Status == ArtifactStatus.InternalApproved)).ToListAsync()) // enums are stored as text, where < would compare names
                a.Status = ArtifactStatus.SentToClient;
        }
        // Older drafts of this reply (an AI rerun, an early save) would keep 초안 있음 and load again into 답장 쓰기.
        if (d.ReplyToMailId is { } replyTo)
            db.MailDrafts.RemoveRange(await db.MailDrafts.Where(x => x.ReplyToMailId == replyTo && x.Id != d.Id && x.Status == "draft").ToListAsync());
        db.Activities.Add(new Activity { ProjectId = d.ProjectId ?? replied?.ProjectId, Kind = "mail", Text = $"메일을 보냈습니다: {d.Subject}" });
        await db.SaveChangesAsync();
        if (replied is not null) await notifier.MailUpdated(await MailEndpoints.SummaryAsync(db, replied.Id));
    }
}
