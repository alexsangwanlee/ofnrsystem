using System.Data.Common;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Ops;
using Ofnr.Server.Realtime;

namespace Ofnr.Server.Mail;

public sealed record FolderInfo(string Name, uint UidValidity);

/// <summary>One IMAP session. Search and fetch act on the folder opened last.</summary>
public interface IImapSource
{
    Task<FolderInfo> OpenAsync(string folder);

    /// <summary>UIDs above <paramref name="afterUid"/>, and when <paramref name="since"/> is set only mail delivered since that day.</summary>
    Task<IReadOnlyList<uint>> SearchNewAsync(uint afterUid, DateTimeOffset? since);

    Task<MimeMessage> FetchAsync(uint uid);

    /// <summary>Full folder names; IsSent when the server marks the folder <c>\Sent</c>.</summary>
    Task<IReadOnlyList<(string Name, bool IsSent)>> ListFoldersAsync();

    /// <summary>IMAP APPEND with the <c>\Seen</c> flag.</summary>
    Task AppendSeenAsync(string folder, MimeMessage m);
}

public interface IMailSync
{
    /// <summary>Syncs INBOX and the Sent folder and returns the number of new mails. Failures throw <see cref="MailException"/> and show in <see cref="Health"/>.</summary>
    Task<int> SyncNowAsync(CancellationToken ct = default);

    CheckDto Health { get; }
}

public sealed class MailSync(IServiceScopeFactory scopes, SettingsStore settings, IOpsNotifier notifier, ILogger<MailSync> logger) : BackgroundService, IMailSync
{
    internal const string NotConfigured = "메일 계정이 설정되지 않았습니다. 설정에서 메일 주소와 앱 비밀번호를 입력해 주세요.";
    const int Batch = 50;

    readonly SemaphoreSlim gate = new(1, 1);
    CheckDto? last;

    public CheckDto Health => settings.Current.Mail.Address is { Length: > 0 } ? last ?? new("메일", true, "첫 동기화를 기다리고 있습니다.") : new("메일", false, NotConfigured);

    /// <summary>The setting, else the folder the server marks <c>\Sent</c>, else one named 보낸메일함 (NAVER WORKS) or Sent.</summary>
    public static async Task<string?> SentFolderAsync(IImapSource imap, MailAccount account)
    {
        if (account.SentFolder is { Length: > 0 } configured) return configured;
        var folders = await imap.ListFoldersAsync();
        return folders.Where(f => f.IsSent).Select(f => f.Name).FirstOrDefault()
            ?? folders.Select(f => f.Name).FirstOrDefault(n => n is "보낸메일함" or "Sent");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield(); // the first IMAP connection must not hold up server start
        while (!stop.IsCancellationRequested)
        {
            // A failure is in Health; the loop keeps going so a server or database that comes back is picked up.
            try
            {
                if (settings.Current.Mail.Address is { Length: > 0 }) await SyncNowAsync(stop);
            }
            catch (MailException e) { logger.LogWarning("Mail sync failed: {Message}", e.Message); }
            // Task.Delay throws above about 49 days, which would stop the whole host.
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(settings.Current.MailSyncMinutes, 1, 1440)), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> SyncNowAsync(CancellationToken ct = default)
    {
        var account = settings.Current.Mail;
        if (account.Address is not { Length: > 0 }) throw new MailException(NotConfigured);
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OfnrDb>();
            var imap = scope.ServiceProvider.GetRequiredService<IImapSource>();
            var sent = await SentFolderAsync(imap, account);
            var added = await SyncFolderAsync(db, imap, "INBOX", outgoing: false, ct);
            if (sent is not null) added += await SyncFolderAsync(db, imap, sent, outgoing: true, ct);
            last = new("메일", true, $"마지막 동기화 {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
            return added;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var message = e is MailException ? e.Message
                : DbError(e) is { } db ? $"데이터베이스에 연결하지 못했습니다: {db.Message}" : $"메일 서버에 연결하지 못했습니다: {e.Message}";
            last = new("메일", false, message);
            throw new MailException(message, e);
        }
        finally { gate.Release(); }
    }

    // EF wraps Npgsql's errors, for example in an InvalidOperationException about transient failures.
    static DbException? DbError(Exception? e) => e is null ? null : e as DbException ?? DbError(e.InnerException);

    async Task<int> SyncFolderAsync(OfnrDb db, IImapSource imap, string folder, bool outgoing, CancellationToken ct)
    {
        var info = await imap.OpenAsync(folder);
        var state = await db.MailSyncStates.FindAsync([folder], ct);
        if (state is null) db.MailSyncStates.Add(state = new MailSyncState { Folder = folder, UidValidity = info.UidValidity });
        // The server renumbered the folder: start over; rows already stored are matched by Message-ID.
        else if (state.UidValidity != info.UidValidity) (state.UidValidity, state.LastUid) = (info.UidValidity, 0);
        var since = state.LastUid == 0 ? DateTimeOffset.UtcNow.AddDays(-30) : (DateTimeOffset?)null;
        var lastUid = state.LastUid;
        // "UID n:*" always matches the newest message, even when its UID is below n.
        var uids = (await imap.SearchNewAsync((uint)lastUid, since)).Where(u => u > lastUid).Order().ToList();
        var projects = (await db.Projects.Where(p => p.Status == ProjectStatus.Active)
                .Select(p => new { p.Id, p.JobNo, Domains = p.Client!.MailDomains }).ToListAsync(ct))
            .Select(p => (p.Id, p.JobNo, p.Domains ?? [])).ToList();
        var added = 0;
        foreach (var chunk in uids.Chunk(Batch))
        {
            var changed = new List<Guid>();
            foreach (var uid in chunk)
            {
                var m = MailMapper.Map(await imap.FetchAsync(uid), folder, info.UidValidity, uid, outgoing);
                if (await StoredAsync(db, m, ct) is { } stored)
                {
                    // Mail to herself, or a Cc to herself, that INBOX brought in first: the copy in Sent shows she wrote it.
                    if (outgoing && !stored.Outgoing)
                    {
                        (stored.Outgoing, stored.Status) = (true, stored.Status == MailStatus.New ? MailStatus.Done : stored.Status);
                        changed.Add(stored.Id);
                    }
                    continue;
                }
                if (MailMapper.MatchProject(m, projects) is { } projectId) (m.ProjectId, m.Status) = (projectId, MailStatus.Linked);
                else if (outgoing) m.Status = MailStatus.Done;
                db.MailMessages.Add(m);
                changed.Add(m.Id);
                added++;
            }
            state.LastUid = chunk[^1];
            await db.SaveChangesAsync(ct);
            // Per chunk, so mail already saved is announced even when a later chunk fails.
            foreach (var dto in await db.MailMessages.Where(m => changed.Contains(m.Id)).ToSummaries().ToListAsync(ct)) await notifier.MailUpdated(dto);
        }
        await db.SaveChangesAsync(ct); // a new or reset state when nothing was fetched
        return added;
    }

    /// <summary>
    /// The stored row of this mail: the same UID, or the same Message-ID in any folder (mail to oneself sits in INBOX and Sent).
    /// A row of this folder under another UID (a UIDVALIDITY change, or mail moved out and back in webmail) takes the new one,
    /// so attachments can still be fetched.
    /// </summary>
    static async Task<MailMessage?> StoredAsync(OfnrDb db, MailMessage m, CancellationToken ct)
    {
        if (await db.MailMessages.FirstOrDefaultAsync(x => x.Folder == m.Folder && x.UidValidity == m.UidValidity && x.Uid == m.Uid, ct) is { } same) return same;
        if (m.MessageId is null) return null;
        var stored = db.MailMessages.Local.FirstOrDefault(x => x.MessageId == m.MessageId)
            ?? await db.MailMessages.FirstOrDefaultAsync(x => x.MessageId == m.MessageId, ct);
        if (stored?.Folder == m.Folder) (stored.UidValidity, stored.Uid) = (m.UidValidity, m.Uid);
        return stored;
    }
}

/// <summary>The NAVER WORKS account over IMAP (SSL on 993, STARTTLS elsewhere). Connects on first use, one per scope.</summary>
public sealed class MailKitImapSource(SettingsStore settings, SecretStore secrets) : IImapSource, IDisposable
{
    readonly ImapClient client = new();
    IMailFolder? open;

    IMailFolder Open => open ?? throw new InvalidOperationException("Open a folder first.");

    async Task<ImapClient> ClientAsync()
    {
        if (client.IsAuthenticated) return client;
        var a = settings.Current.Mail;
        var password = secrets.Load("mail") ?? throw new MailException("메일 비밀번호가 설정되지 않았습니다.");
        if (!client.IsConnected) await client.ConnectAsync(a.ImapHost, a.ImapPort, a.ImapPort == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(a.Address, password);
        return client;
    }

    public async Task<FolderInfo> OpenAsync(string folder)
    {
        var c = await ClientAsync();
        open = folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? c.Inbox : await c.GetFolderAsync(folder);
        await open.OpenAsync(FolderAccess.ReadOnly);
        return new(folder, open.UidValidity);
    }

    public async Task<IReadOnlyList<uint>> SearchNewAsync(uint afterUid, DateTimeOffset? since)
    {
        var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), UniqueId.MaxValue));
        var uids = await Open.SearchAsync(since is { } s ? query.And(SearchQuery.DeliveredAfter(s.UtcDateTime)) : query);
        return [.. uids.Select(u => u.Id)];
    }

    public async Task<MimeMessage> FetchAsync(uint uid) => await Open.GetMessageAsync(new UniqueId(uid)); // BODY.PEEK[]: stays unread

    public async Task<IReadOnlyList<(string Name, bool IsSent)>> ListFoldersAsync()
    {
        var c = await ClientAsync();
        // GetFolder(SpecialFolder) throws without SPECIAL-USE or XLIST and returns null when the server has no such folder.
        var sent = (c.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0 ? c.GetFolder(SpecialFolder.Sent)?.FullName : null;
        var folders = await c.GetFoldersAsync(c.PersonalNamespaces[0]);
        return [.. folders.Select(f => (f.FullName, f.FullName == sent || f.Attributes.HasFlag(FolderAttributes.Sent)))];
    }

    public async Task AppendSeenAsync(string folder, MimeMessage m)
    {
        var c = await ClientAsync();
        await (await c.GetFolderAsync(folder)).AppendAsync(new AppendRequest(m, MessageFlags.Seen));
    }

    public void Dispose() => client.Dispose();
}
