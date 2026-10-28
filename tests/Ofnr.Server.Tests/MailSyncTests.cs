using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Mail;
using Ofnr.Server.Ops;

namespace Ofnr.Server.Tests;

/// <summary>An IMAP account in memory. Like a real server answering <c>UID n:*</c>, a search returns the newest message even when its UID is below n.</summary>
public sealed class FakeImap : IImapSource
{
    readonly Dictionary<string, SortedDictionary<uint, MimeMessage>> folders = new() { ["INBOX"] = [], ["보낸메일함"] = [] };
    string open = "";
    int lists;

    public uint UidValidity { get; set; } = 1;
    public Exception? Fail { get; set; }
    public int Lists => lists;
    public List<(string Folder, uint After, DateTimeOffset? Since)> Searches { get; } = [];
    public List<(string Folder, MimeMessage Message)> Appended { get; } = [];

    /// <summary>No \Sent attribute, as on a server without SPECIAL-USE: the Sent folder is found by its name.</summary>
    public List<(string Name, bool IsSent)> Folders { get; set; } = [("INBOX", false), ("보낸메일함", false), ("스팸메일함", false)];

    public void Put(string folder, uint uid, MimeMessage m)
    {
        lock (folders) folders[folder][uid] = m;
    }

    public void Clear(string folder)
    {
        lock (folders) folders[folder].Clear();
    }

    public Task<IReadOnlyList<(string Name, bool IsSent)>> ListFoldersAsync()
    {
        Interlocked.Increment(ref lists);
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<(string, bool)>>([.. Folders]);
    }

    public Task<FolderInfo> OpenAsync(string folder)
    {
        open = folder;
        return Task.FromResult(new FolderInfo(folder, UidValidity));
    }

    public Task<IReadOnlyList<uint>> SearchNewAsync(uint afterUid, DateTimeOffset? since)
    {
        lock (folders)
        {
            Searches.Add((open, afterUid, since));
            var uids = folders[open].Keys.Where(u => u > afterUid).ToList();
            if (uids.Count == 0 && folders[open].Count > 0) uids.Add(folders[open].Keys.Last());
            return Task.FromResult<IReadOnlyList<uint>>(uids);
        }
    }

    /// <summary>Like MailKit for a UID the server no longer has. A message put as null is found by search but gone on fetch.</summary>
    public Task<MimeMessage> FetchAsync(uint uid)
    {
        lock (folders) return Task.FromResult(folders[open].GetValueOrDefault(uid) ?? throw new MessageNotFoundException("The IMAP server did not return the requested message."));
    }

    public Task AppendSeenAsync(string folder, MimeMessage m)
    {
        lock (folders) Appended.Add((folder, m));
        return Task.CompletedTask;
    }
}

public sealed class FakeSmtp : ISmtpTransport
{
    readonly List<MimeMessage> sent = [];
    public List<MimeMessage> Sent { get { lock (sent) return [.. sent]; } }

    public Task SendAsync(MimeMessage m)
    {
        lock (sent) sent.Add(m);
        return Task.CompletedTask;
    }
}

public sealed class MailSyncTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A MailSync on a fake account and its own database, or on <paramref name="connectionString"/>. Its loop starts only on StartAsync.</summary>
    sealed class Rig : IDisposable
    {
        readonly string data = Directory.CreateTempSubdirectory("ofnr-m-").FullName;
        readonly ServiceProvider services;

        public Rig(string? connectionString = null)
        {
            Db = connectionString is null ? TestDb.Create() : null;
            File.WriteAllText(Path.Combine(data, "settings.json"), """{ "mail": { "address": "ceo@ofnr.kr" } }""");
            Settings = new SettingsStore(new ConfigurationBuilder().AddInMemoryCollection([new("OFNR_DATA", data)]).Build());
            services = new ServiceCollection()
                .AddDbContext<OfnrDb>(o => o.UseNpgsql(connectionString ?? Db!.ConnectionString))
                .AddSingleton<IImapSource>(Imap)
                .BuildServiceProvider();
            Triage = new MailTriage(services.GetRequiredService<IServiceScopeFactory>(), Settings, Agents, Notifier, NullLogger<MailTriage>.Instance);
            Sync = new MailSync(services.GetRequiredService<IServiceScopeFactory>(), Settings, Notifier, Triage, NullLogger<MailSync>.Instance);
        }

        public SettingsStore Settings { get; }
        public FakeAgents Agents { get; } = new();

        /// <summary>Not started unless a test starts it, so queued mail waits.</summary>
        public MailTriage Triage { get; }

        public TestDb? Db { get; }
        public FakeImap Imap { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public MailSync Sync { get; }

        public async Task<Guid> AddProject(string jobNo, string domain)
        {
            await using var db = Db!.Context();
            var p = new Project { JobNo = jobNo, Name = "성수 팝업", Slug = "P", FolderPath = data, Client = new Client { Name = "TNF", MailDomains = [domain] } };
            db.Projects.Add(p);
            await db.SaveChangesAsync(Ct);
            return p.Id;
        }

        public async Task<List<MailMessage>> Rows()
        {
            await using var db = Db!.Context();
            return await db.MailMessages.AsNoTracking().OrderBy(m => m.Uid).ToListAsync(Ct);
        }

        public async Task<MailSyncState> State(string folder)
        {
            await using var db = Db!.Context();
            return await db.MailSyncStates.SingleAsync(s => s.Folder == folder, Ct);
        }

        public void Dispose()
        {
            Sync.Dispose();
            Triage.Dispose();
            services.Dispose();
            Db?.Dispose();
            Directory.Delete(data, true);
        }
    }

    static MimeMessage Msg(string messageId, string subject, string from = "kim@tnf.co.kr")
    {
        var m = new MimeMessage { Subject = subject, MessageId = messageId, Body = new TextPart("plain") { Text = "본문입니다." } };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(from == "ceo@ofnr.kr" ? "kim@tnf.co.kr" : "ceo@ofnr.kr"));
        return m;
    }

    [Fact]
    public async Task Same_uid_synced_twice_is_one_row()
    {
        using var rig = new Rig();
        var project = await rig.AddProject("OFNR-26001", "tnf.co.kr");
        rig.Imap.Put("INBOX", 5, Msg("a@tnf.co.kr", "견적 요청"));
        // Mail the CEO sent to herself: the same Message-ID in INBOX and Sent.
        rig.Imap.Put("INBOX", 6, Msg("self@ofnr.kr", "메모", from: "ceo@ofnr.kr"));
        rig.Imap.Put("보낸메일함", 3, Msg("self@ofnr.kr", "메모", from: "ceo@ofnr.kr"));
        rig.Imap.Put("보낸메일함", 4, Msg("r@ofnr.kr", "RE: 견적 요청", from: "ceo@ofnr.kr"));

        Assert.Equal(3, await rig.Sync.SyncNowAsync(Ct));
        Assert.Equal(0, await rig.Sync.SyncNowAsync(Ct)); // the account still answers UID 7:* with UID 6

        var rows = await rig.Rows();
        Assert.Equal(["r@ofnr.kr", "a@tnf.co.kr", "self@ofnr.kr"], rows.Select(m => m.MessageId));
        var inbound = rows.Single(m => m.MessageId == "a@tnf.co.kr");
        Assert.Equal((project, MailStatus.Linked, false, "INBOX"), (inbound.ProjectId, inbound.Status, inbound.Outgoing, inbound.Folder));
        Assert.True(rows.Single(m => m.MessageId == "r@ofnr.kr").Outgoing);
        // INBOX brought the mail to herself in first; its copy in Sent shows she wrote it, so it is not left as unsorted mail.
        var self = rows.Single(m => m.MessageId == "self@ofnr.kr");
        Assert.Equal((true, MailStatus.Done), (self.Outgoing, self.Status));
        // The first sync looks back 30 days, later ones only past the last UID.
        Assert.Equal([(0u, true), (6u, false)], rig.Imap.Searches.Where(s => s.Folder == "INBOX").Select(s => (s.After, s.Since is not null)));
        var state = await rig.State("INBOX");
        Assert.Equal((1L, 6L), (state.UidValidity, state.LastUid));
        var notified = rig.Notifier.Calls.OfType<MailSummaryDto>().ToList();
        Assert.Equal(4, notified.Count);
        Assert.Equal(MailStatus.Done, notified.Last(d => d.Id == self.Id).Status);
        Assert.True(rig.Sync.Health.Ok);
    }

    [Fact]
    public async Task New_incoming_mail_is_queued_for_triage()
    {
        using var rig = new Rig();
        rig.Settings.Current.PluginDir = Path.Combine(AppContext.BaseDirectory, "claude-plugin");
        rig.Agents.Script = (_, _) => throw new InvalidOperationException("Claude 실행 오류: Not logged in");
        await rig.Triage.StartAsync(Ct);
        rig.Imap.Put("INBOX", 1, Msg("a@tnf.co.kr", "견적 요청"));
        rig.Imap.Put("보낸메일함", 1, Msg("r@ofnr.kr", "RE: 견적 요청", from: "ceo@ofnr.kr"));

        await rig.Sync.SyncNowAsync(Ct);
        await JobHost.WaitFor(() => rig.Triage.Failure is not null);

        var rows = await rig.Rows();
        var prompt = Assert.Single(rig.Agents.Calls).Prompt;
        Assert.Contains(rows.Single(m => !m.Outgoing).Id.ToString(), prompt);
        Assert.DoesNotContain(rows.Single(m => m.Outgoing).Id.ToString(), prompt);
        Assert.Equal(new CheckDto("메일", false, "메일 분류에 실패했습니다: Claude 실행 오류: Not logged in"), rig.Sync.Health);
        // With AutoTriage off nothing would clear the failure.
        rig.Settings.Current.AutoTriage = false;
        Assert.True(rig.Sync.Health.Ok);
        await rig.Triage.StopAsync(Ct);
    }

    [Fact]
    public async Task Uidvalidity_change_dedupes_by_message_id()
    {
        using var rig = new Rig();
        var project = await rig.AddProject("OFNR-26001", "tnf.co.kr");
        rig.Imap.Put("INBOX", 5, Msg("a@tnf.co.kr", "견적 요청"));
        await rig.Sync.SyncNowAsync(Ct);
        await using (var db = rig.Db!.Context())
        {
            (await db.MailMessages.SingleAsync(Ct)).Status = MailStatus.NeedsReply;
            await db.SaveChangesAsync(Ct);
        }

        // The server renumbers the folder.
        rig.Imap.UidValidity = 2;
        rig.Imap.Clear("INBOX");
        rig.Imap.Put("INBOX", 1, Msg("a@tnf.co.kr", "견적 요청"));
        rig.Imap.Put("INBOX", 2, Msg("b@tnf.co.kr", "추가 자료"));

        Assert.Equal(1, await rig.Sync.SyncNowAsync(Ct));

        var rows = await rig.Rows();
        Assert.Equal(2, rows.Count);
        var a = rows.Single(m => m.MessageId == "a@tnf.co.kr");
        // Re-pointed at its new UID, so its attachments can still be fetched, with its link and status kept.
        Assert.Equal((2L, 1L, project, MailStatus.NeedsReply), (a.UidValidity, a.Uid, a.ProjectId, a.Status));
        Assert.Equal((0u, true), rig.Imap.Searches.Where(s => s.Folder == "INBOX").Select(s => (s.After, s.Since is not null)).Last());
        var state = await rig.State("INBOX");
        Assert.Equal((2L, 2L), (state.UidValidity, state.LastUid));

        // Moved out of INBOX and back in webmail: the same UIDVALIDITY, a new UID.
        rig.Imap.Put("INBOX", 3, Msg("a@tnf.co.kr", "견적 요청"));
        Assert.Equal(0, await rig.Sync.SyncNowAsync(Ct));
        Assert.Equal(3L, (await rig.Rows()).Single(m => m.MessageId == "a@tnf.co.kr").Uid);
    }

    [Fact]
    public async Task Each_saved_chunk_is_announced_when_a_later_one_fails()
    {
        using var rig = new Rig();
        for (uint uid = 1; uid <= 50; uid++) rig.Imap.Put("INBOX", uid, Msg($"{uid}@tnf.co.kr", "견적 요청"));
        rig.Imap.Put("INBOX", 51, null!); // deleted between search and fetch

        await Assert.ThrowsAsync<MailException>(() => rig.Sync.SyncNowAsync(Ct));

        Assert.Equal(50, (await rig.Rows()).Count);
        Assert.Equal(50, rig.Notifier.Calls.OfType<MailSummaryDto>().Count());
        Assert.Equal(50L, (await rig.State("INBOX")).LastUid);
    }

    [Fact]
    public async Task Missing_sent_folder_syncs_inbox_and_shows_in_health()
    {
        using var rig = new Rig();
        rig.Imap.Folders = [("INBOX", false), ("Sent Messages", false)];
        rig.Imap.Put("INBOX", 1, Msg("a@tnf.co.kr", "견적 요청"));

        Assert.Equal(1, await rig.Sync.SyncNowAsync(Ct));

        Assert.Equal(new CheckDto("메일", false, "보낸메일함을 찾지 못했습니다. 설정 > 메일의 '보낸메일함 이름'에 NAVER WORKS에 보이는 이름을 입력해 주세요."), rig.Sync.Health);
        Assert.DoesNotContain(rig.Imap.Searches, s => s.Folder != "INBOX");
    }

    [Fact]
    public async Task Unreachable_mail_server_or_database_sets_health_and_the_service_keeps_running()
    {
        using var noServer = new Rig();
        noServer.Imap.Fail = new IOException("연결 시간이 초과되었습니다.");
        var e = await Assert.ThrowsAsync<MailException>(() => noServer.Sync.SyncNowAsync(Ct));
        Assert.Equal("메일 서버에 연결하지 못했습니다: 연결 시간이 초과되었습니다.", e.Message);
        Assert.Equal(new CheckDto("메일", false, e.Message), noServer.Sync.Health);

        using var noDb = new Rig("Host=127.0.0.1;Port=1;Username=ofnr;Password=x;Database=x");
        noDb.Imap.Put("INBOX", 1, Msg("a@tnf.co.kr", "견적 요청"));
        Assert.StartsWith("데이터베이스에 연결하지 못했습니다: ", (await Assert.ThrowsAsync<MailException>(() => noDb.Sync.SyncNowAsync(Ct))).Message);

        using var noPassword = new Rig();
        noPassword.Imap.Fail = new MailException("메일 비밀번호가 설정되지 않았습니다.");
        Assert.Equal("메일 비밀번호가 설정되지 않았습니다.", (await Assert.ThrowsAsync<MailException>(() => noPassword.Sync.SyncNowAsync(Ct))).Message);
        Assert.Equal("메일 비밀번호가 설정되지 않았습니다.", noPassword.Sync.Health.Detail);

        foreach (var rig in new[] { noServer, noDb })
        {
            rig.Settings.Current.MailSyncMinutes = 100_000; // above what Task.Delay accepts
            var before = rig.Imap.Lists;
            await rig.Sync.StartAsync(Ct);
            await JobHost.WaitFor(() => rig.Imap.Lists > before); // the loop's first sync started and fails
            await Task.Delay(200, Ct);
            Assert.False(rig.Sync.ExecuteTask!.IsCompleted);
            await rig.Sync.StopAsync(Ct);
        }
    }
}
