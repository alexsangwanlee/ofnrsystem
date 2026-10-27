using System.Text.Json;
using MailKit;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Ofnr.Server.Data;
using Ofnr.Server.Projects;
using Ofnr.Server.Realtime;
using Activity = Ofnr.Server.Data.Activity;

namespace Ofnr.Server.Mail;

public static class MailEndpoints
{
    static readonly JsonElement EmptyObject = JsonElement.Parse("{}");

    // One send at a time, so a double click cannot reach the client twice.
    static readonly SemaphoreSlim SendLock = new(1, 1);

    public static IQueryable<MailSummaryDto> ToSummaries(this IQueryable<MailMessage> q) => q.Select(m => new MailSummaryDto(
        m.Id, m.FromName, m.FromAddress, m.Subject, m.ReceivedAt, m.Status, m.Category, m.Summary,
        m.ProjectId, m.Project != null ? m.Project.JobNo : null, m.SuggestedProjectId, m.SuggestedConfidence, m.Attachments.Count > 0,
        m.Drafts.Any(d => d.Status == "draft")));

    public static Task<MailSummaryDto> SummaryAsync(OfnrDb db, Guid id) => db.MailMessages.Where(m => m.Id == id).ToSummaries().SingleAsync();

    public static void MapMailEndpoints(this WebApplication app)
    {
        app.MapGet("/api/mail", async (OfnrDb db, MailStatus? status, Guid? projectId, string? q, int? skip, int? take) =>
        {
            var query = db.MailMessages.Where(m => (status == null || m.Status == status) && (projectId == null || m.ProjectId == projectId));
            if (!string.IsNullOrWhiteSpace(q))
            {
                // ILIKE finds two-syllable Korean words such as 견적, which trigram matching misses. % and _ match only themselves.
                var pattern = "%" + q.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
                query = query.Where(m => EF.Functions.ILike(m.Subject, pattern, @"\") || EF.Functions.ILike(m.FromAddress, pattern, @"\")
                    || EF.Functions.ILike(m.FromName, pattern, @"\") || (m.TextBody != null && EF.Functions.ILike(m.TextBody, pattern, @"\")));
            }
            return await query.OrderByDescending(m => m.ReceivedAt).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 200)).ToSummaries().ToListAsync();
        });

        app.MapGet("/api/mail/{id:guid}", async (OfnrDb db, Guid id) =>
        {
            if (await db.MailMessages.AsNoTracking().Include(m => m.Project).SingleOrDefaultAsync(m => m.Id == id) is not { } m) return MailNotFound();
            // A subject key can match another client's mail, so it joins only mail of the same project, as get_mail_thread does.
            var bySubject = m.ThreadKey.StartsWith("subj:");
            var thread = await db.MailMessages
                .Where(x => x.Id == id || (x.ThreadKey == m.ThreadKey && (!bySubject || (m.ProjectId != null && x.ProjectId == m.ProjectId))))
                .OrderBy(x => x.ReceivedAt).ToSummaries().ToListAsync();
            return Results.Ok(new MailDetailDto(
                m.Id, m.FromName, m.FromAddress, m.Subject, m.ReceivedAt, m.Status, m.Category, m.Summary, m.ProjectId, m.Project?.JobNo,
                m.SuggestedProjectId, m.SuggestedConfidence, m.Attachments.Count > 0, thread.Single(t => t.Id == id).HasDraft, m.To, m.Cc, m.TextBody, m.HtmlBody, m.Attachments,
                m.Extracted?.RootElement ?? EmptyObject, thread));
        });

        app.MapPost("/api/mail/{id:guid}/link", async (OfnrDb db, IOpsNotifier ops, Guid id, MailLinkInput input) =>
        {
            if (await db.MailMessages.FindAsync(id) is not { } m) return MailNotFound();
            if (input.ProjectId is not { } projectId) return Error(400, "연결할 프로젝트를 선택해 주세요.");
            if (!await db.Projects.AnyAsync(p => p.Id == projectId)) return Error(404, "프로젝트를 찾을 수 없습니다.");
            Link(db, m, projectId);
            await db.SaveChangesAsync();
            return Results.Ok(await NotifyAsync(db, ops, id));
        });

        app.MapPost("/api/mail/{id:guid}/status", async (OfnrDb db, IOpsNotifier ops, Guid id, MailStatusInput input) =>
        {
            if (input.Status is not { } status || !Enum.IsDefined(status)) return Error(400, "알 수 없는 메일 상태입니다.");
            if (await db.MailMessages.FindAsync(id) is not { } m) return MailNotFound();
            m.Status = status;
            await db.SaveChangesAsync();
            return Results.Ok(await NotifyAsync(db, ops, id));
        });

        app.MapPost("/api/mail/{id:guid}/attachments/{index:int}/save", async (OfnrDb db, IImapSource imap, Guid id, int index) =>
        {
            if (await db.MailMessages.Include(m => m.Project).SingleOrDefaultAsync(m => m.Id == id) is not { } mail) return MailNotFound();
            if (mail.Project is not { } project) return Error(400, "먼저 메일을 프로젝트에 연결해 주세요.");
            MimeMessage message;
            try
            {
                if ((await imap.OpenAsync(mail.Folder)).UidValidity != mail.UidValidity)
                    return Error(409, "메일함이 바뀌어 첨부를 가져오지 못했습니다. 메일을 다시 동기화해 주세요.");
                message = await imap.FetchAsync((uint)mail.Uid);
            }
            catch (MessageNotFoundException) { return Error(409, "이 메일이 메일함에 더 이상 없습니다. 웹메일에서 옮기거나 지웠는지 확인해 주세요."); }
            catch (Exception e) when (e is not OperationCanceledException) { return Error(409, $"메일 서버에 연결하지 못했습니다: {e.Message}"); }
            var attachments = MailMapper.Attachments(message);
            if (index < 0 || index >= attachments.Count) return Error(404, "첨부 파일을 찾을 수 없습니다.");
            try
            {
                var dir = Path.Combine(project.FolderPath, "00_IN");
                Directory.CreateDirectory(dir);
                var (path, stream) = CreateUnique(dir, SafeFileName(MailMapper.FileName(attachments[index])));
                using (stream) MailMapper.Decode(attachments[index], stream);
                var file = new FileInfo(path);
                return Results.Ok(new FileDto(Path.GetRelativePath(project.FolderPath, path).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc, null, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Error(400, $"첨부를 저장하지 못했습니다: {e.Message}"); }
        });

        app.MapPost("/api/mail/{id:guid}/create-project", async (OfnrDb db, SettingsStore settings, IOpsNotifier ops, Guid id, ProjectInput input) =>
        {
            if (await db.MailMessages.FindAsync(id) is not { } m) return MailNotFound();
            if (await ProjectEndpoints.ValidateAsync(db, input) is { } error) return Error(400, error);
            Project p;
            try { p = await ProjectEndpoints.CreateAsync(db, settings.Current, input); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ProjectEndpoints.FolderFailed(e); }
            Link(db, m, p.Id);
            await db.SaveChangesAsync();
            var dto = await ProjectEndpoints.DtoAsync(db, p.Id);
            await ops.ProjectUpdated(dto);
            await NotifyAsync(db, ops, id);
            return Results.Ok(dto);
        });

        app.MapPost("/api/mail/sync", async (IMailSync sync) =>
        {
            try { return Results.Ok(new { added = await sync.SyncNowAsync() }); }
            catch (MailException e) { return Error(409, e.Message); }
        });

        app.MapGet("/api/mail/drafts", async (OfnrDb db, Guid? mailId) =>
            (await db.MailDrafts.Where(d => mailId == null || d.ReplyToMailId == mailId).OrderByDescending(d => d.CreatedAt).ToListAsync()).Select(MailDraftDto.From));

        app.MapPost("/api/mail/drafts", (OfnrDb db, DraftInput input) => SaveDraftAsync(db, new MailDraft(), input));

        app.MapPut("/api/mail/drafts/{id:guid}", async (OfnrDb db, Guid id, DraftInput input) =>
            await db.MailDrafts.FindAsync(id) is not { } d ? DraftNotFound()
            : d.Status == "sent" ? AlreadySent()
            : await SaveDraftAsync(db, d, input));

        app.MapPost("/api/mail/drafts/{id:guid}/send", async (OfnrDb db, MailSender sender, Guid id) =>
        {
            await SendLock.WaitAsync();
            try
            {
                if (await db.MailDrafts.FindAsync(id) is not { } d) return DraftNotFound();
                if (d.Status == "sent") return AlreadySent();
                try { await sender.SendAsync(d); }
                catch (MailException e) { return Error(400, e.Message); }
                return Results.Ok(MailDraftDto.From(d));
            }
            finally { SendLock.Release(); }
        });
    }

    static void Link(OfnrDb db, MailMessage m, Guid projectId)
    {
        (m.ProjectId, m.Status) = (projectId, MailStatus.Linked);
        db.Activities.Add(new Activity { ProjectId = projectId, Kind = "mail", Text = $"메일을 연결했습니다: {m.Subject}" });
    }

    static async Task<MailSummaryDto> NotifyAsync(OfnrDb db, IOpsNotifier ops, Guid id)
    {
        var dto = await SummaryAsync(db, id);
        await ops.MailUpdated(dto);
        return dto;
    }

    static async Task<IResult> SaveDraftAsync(OfnrDb db, MailDraft d, DraftInput i)
    {
        if (string.IsNullOrWhiteSpace(i.To)) return Error(400, "받는 사람을 입력해 주세요.");
        var projectId = i.ProjectId;
        if (i.ReplyToMailId is { } mailId)
        {
            if (await db.MailMessages.FindAsync(mailId) is not { } mail) return MailNotFound();
            projectId ??= mail.ProjectId;
        }
        if (projectId is { } p && !await db.Projects.AnyAsync(x => x.Id == p)) return Error(404, "프로젝트를 찾을 수 없습니다.");
        (d.ReplyToMailId, d.ProjectId, d.To, d.Cc, d.Subject, d.Body, d.Attachments, d.UpdatedAt) =
            (i.ReplyToMailId, projectId, i.To.Trim(), string.IsNullOrWhiteSpace(i.Cc) ? null : i.Cc.Trim(), i.Subject ?? "", i.Body ?? "", i.Attachments ?? [], DateTimeOffset.UtcNow);
        if (db.Entry(d).State == EntityState.Detached) db.MailDrafts.Add(d);
        await db.SaveChangesAsync();
        return Results.Ok(MailDraftDto.From(d));
    }

    /// <summary>The attachment's own name, Korean kept: no path separators, no characters Windows forbids, no leading or trailing dots.</summary>
    static string SafeFileName(string name)
    {
        var kept = new string([.. name.Where(c => !char.IsControl(c) && !@"\/:*?""<>|".Contains(c))]).Trim().Trim('.', ' ');
        return kept.Length == 0 ? "첨부 파일" : kept;
    }

    /// <summary>A new file: name.ext, else name_2.ext, name_3.ext and so on. Never overwrites.</summary>
    static (string Path, FileStream Stream) CreateUnique(string dir, string fileName)
    {
        var (stem, ext) = (Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(dir, n == 1 ? fileName : $"{stem}_{n}{ext}");
            try { return (path, new FileStream(path, FileMode.CreateNew)); }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    static IResult MailNotFound() => Error(404, "메일을 찾을 수 없습니다.");

    static IResult DraftNotFound() => Error(404, "초안을 찾을 수 없습니다.");

    static IResult AlreadySent() => Error(409, "이미 보낸 메일입니다.");

    static IResult Error(int status, string message) => ProjectEndpoints.Error(status, message);
}
