using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;
using Activity = Ofnr.Server.Data.Activity;

namespace Ofnr.Server.Projects;

public static class ProjectEndpoints
{
    static readonly string[] StageLabels = ["문의", "기획", "디자인", "제작", "운영", "리포트", "완료"];

    // Serializes number allocation in this process; the unique index and one retry cover a second server process.
    static readonly SemaphoreSlim CreateLock = new(1, 1);

    static readonly JobStatus[] AttentionStatuses = [JobStatus.NeedsAttention, JobStatus.Review, JobStatus.Failed];

    static readonly HashSet<string> OpenableExtensions = new(
        "xlsx xls xlsm pptx ppt docx doc pdf png jpg jpeg gif webp svg ai psd skp layout dwg dxf txt md csv zip mp4 mov hwp hwpx".Split(' ').Select(e => "." + e),
        StringComparer.OrdinalIgnoreCase);

    public static string[] StageSuggestions(ProjectStage stage) => stage switch
    {
        ProjectStage.Inquiry => ["brief", "reply"],
        ProjectStage.Planning => ["proposal", "quote", "document"],
        ProjectStage.Design => ["model3d", "graphic"],
        ProjectStage.Production => ["quote"],
        ProjectStage.Operation => ["ops-manual"],
        ProjectStage.Report => ["report"],
        _ => [],
    };

    public static IQueryable<ProjectDto> ToDtos(this IQueryable<Project> q, OfnrDb db) => q.Select(p => new ProjectDto(
        p.Id, p.JobNo, p.Name, p.Slug, p.ClientId, p.Client != null ? p.Client.Name : null, p.Stage, p.Status, p.Venue,
        p.InstallDate, p.OpenDate, p.EndDate, p.TeardownDate, p.QuoteAmount, p.ContractAmount, p.CostAmount, p.FolderPath,
        db.Jobs.Count(j => j.ProjectId == p.Id && AttentionStatuses.Contains(j.Status)),
        db.Jobs.Count(j => j.ProjectId == p.Id && j.Status == JobStatus.Running), p.Instructions));

    public static Task<ProjectDto> DtoAsync(OfnrDb db, Guid id) => db.Projects.Where(p => p.Id == id).ToDtos(db).SingleAsync();

    public static async Task<string?> ValidateAsync(OfnrDb db, ProjectInput i) =>
        string.IsNullOrWhiteSpace(i.Name) ? "프로젝트 이름을 입력해 주세요."
        // The enum converter still reads comma lists such as "Report, Done" into undefined values.
        : i.Stage is { } s && !Enum.IsDefined(s) ? "알 수 없는 단계입니다."
        : i.ClientId is { } c && !await db.Clients.AnyAsync(x => x.Id == c) ? "거래처를 찾을 수 없습니다."
        : null;

    /// <summary>Allocates the job number, creates the folder and saves the project. Input must pass <see cref="ValidateAsync"/>.</summary>
    public static async Task<Project> CreateAsync(OfnrDb db, OfnrOptions o, ProjectInput input)
    {
        var now = DateTimeOffset.Now;
        var p = new Project
        {
            Slug = ProjectFolders.MakeSlug(string.IsNullOrWhiteSpace(input.Slug) ? input.Name! : input.Slug),
            Stage = input.Stage ?? ProjectStage.Inquiry,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(p, input);
        db.Projects.Add(p);
        db.Activities.Add(new Activity { ProjectId = p.Id, Kind = "project", Text = $"프로젝트를 만들었습니다: {p.Name}" });
        await CreateLock.WaitAsync();
        try
        {
            for (var retry = true; ; retry = false)
            {
                p.JobNo = await JobNumbers.NextAsync(db, now);
                // shortcut: a race lost to another server process leaves this empty folder behind; fine while one server runs.
                p.FolderPath = ProjectFolders.Create(o, p.JobNo, p.Slug, now);
                try
                {
                    await db.SaveChangesAsync();
                    return p;
                }
                catch (DbUpdateException e) when (retry && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    // Another process took the number between NextAsync and the insert.
                }
            }
        }
        finally { CreateLock.Release(); }
    }

    public static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapGet("/api/clients", async (OfnrDb db) => (await db.Clients.OrderBy(c => c.Name).ToListAsync()).Select(ClientDto.From));

        app.MapPost("/api/clients", async (OfnrDb db, ClientInput input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Error(400, "거래처 이름을 입력해 주세요.");
            var c = new Client();
            Apply(c, input);
            db.Clients.Add(c);
            await db.SaveChangesAsync();
            return Results.Ok(ClientDto.From(c));
        });

        app.MapPut("/api/clients/{id:guid}", async (OfnrDb db, Guid id, ClientInput input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Error(400, "거래처 이름을 입력해 주세요.");
            if (await db.Clients.FindAsync(id) is not { } c) return Error(404, "거래처를 찾을 수 없습니다.");
            Apply(c, input);
            await db.SaveChangesAsync();
            return Results.Ok(ClientDto.From(c));
        });

        app.MapGet("/api/projects", async (OfnrDb db, ProjectStage? stage, ProjectStatus? status) =>
            await db.Projects
                .Where(p => (stage == null || p.Stage == stage) && (status == null || p.Status == status))
                .OrderByDescending(p => p.CreatedAt)
                .ToDtos(db)
                .ToListAsync());

        app.MapPost("/api/projects", async (OfnrDb db, SettingsStore settings, IOpsNotifier ops, ProjectInput input) =>
        {
            if (await ValidateAsync(db, input) is { } error) return Error(400, error);
            Project p;
            try { p = await CreateAsync(db, settings.Current, input); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Error(400, $"프로젝트 폴더를 만들지 못했습니다. 설정에서 프로젝트 폴더를 확인해 주세요: {e.Message}");
            }
            var dto = await DtoAsync(db, p.Id);
            await ops.ProjectUpdated(dto);
            return Results.Ok(dto);
        });

        app.MapGet("/api/projects/{id:guid}", async (OfnrDb db, Guid id) =>
            await DetailAsync(db, id) is { } d ? Results.Ok(d) : ProjectNotFound());

        // The folder name and stage stay as they are: the folder holds the files, and stage moves go through /stage.
        app.MapPut("/api/projects/{id:guid}", async (OfnrDb db, IOpsNotifier ops, Guid id, ProjectInput input) =>
        {
            if (await db.Projects.FindAsync(id) is not { } p) return ProjectNotFound();
            if (await ValidateAsync(db, input) is { } error) return Error(400, error);
            Apply(p, input);
            p.UpdatedAt = DateTimeOffset.UtcNow;
            db.Activities.Add(new Activity { ProjectId = id, Kind = "project", Text = "프로젝트 정보를 수정했습니다." });
            await db.SaveChangesAsync();
            var detail = (await DetailAsync(db, id))!;
            await ops.ProjectUpdated(detail.Project);
            return Results.Ok(detail);
        });

        app.MapPost("/api/projects/{id:guid}/stage", async (OfnrDb db, IOpsNotifier ops, Guid id, StageInput input) =>
        {
            if (input.Stage is not { } stage || !Enum.IsDefined(stage)) return Error(400, "옮길 단계를 선택해 주세요.");
            if (await db.Projects.FindAsync(id) is not { } p) return ProjectNotFound();
            // The contract gate between planning and design only warns; it never blocks. A drag from 문의 straight to 제작 crosses it too.
            string[] warnings = p.Stage < ProjectStage.Design && stage >= ProjectStage.Design
                && !await db.Artifacts.AnyAsync(a => a.ProjectId == id && (a.DocCode == "QUOTE" || a.DocCode == "PROP"))
                ? ["견적서나 제안서 없이 디자인 단계로 넘어갑니다."]
                : [];
            if (p.Stage != stage)
            {
                db.Activities.Add(new Activity { ProjectId = id, Kind = "stage", Text = $"단계를 옮겼습니다: {StageLabels[(int)p.Stage]} → {StageLabels[(int)stage]}" });
                p.Stage = stage;
                p.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            var dto = await DtoAsync(db, id);
            await ops.ProjectUpdated(dto);
            // shortcut: name, engines and lock are placeholders until Task 4's RecipeCatalog can fill them.
            var suggestions = StageSuggestions(stage).Select(r => new RecipeDto(r, r, stage, [], null, true)).ToArray();
            return Results.Ok(new StageMoveDto(dto, suggestions, warnings));
        });

        app.MapGet("/api/projects/{id:guid}/files", async (OfnrDb db, Guid id) =>
        {
            var folder = await db.Projects.Where(p => p.Id == id).Select(p => p.FolderPath).SingleOrDefaultAsync();
            if (folder is null) return ProjectNotFound();
            var artifacts = (await db.Artifacts.Where(a => a.ProjectId == id).OrderByDescending(a => a.CreatedAt).ToListAsync())
                .DistinctBy(a => a.RelativePath)
                .ToDictionary(a => a.RelativePath);
            return Results.Ok(ProjectFolders.Snapshot(folder).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f =>
            {
                var a = artifacts.GetValueOrDefault(f.Key);
                return new FileDto(f.Key, f.Value.size, new DateTimeOffset(f.Value.mtimeUtc), a?.Id, a?.Status);
            }));
        });

        app.MapPost("/api/open", (SettingsStore settings, ILoggerFactory logs, OpenInput input) =>
            Open(settings.Current, logs.CreateLogger("Ofnr.Open"), input.Path));
    }

    static async Task<ProjectDetailDto?> DetailAsync(OfnrDb db, Guid id)
    {
        if (await db.Projects.Where(p => p.Id == id).ToDtos(db).SingleOrDefaultAsync() is not { } project) return null;
        var mails = await db.MailMessages.Where(m => m.ProjectId == id).OrderByDescending(m => m.ReceivedAt)
            .Select(m => new MailSummaryDto(m.Id, m.FromName, m.FromAddress, m.Subject, m.ReceivedAt, m.Status, m.Category, m.Summary,
                m.ProjectId, project.JobNo, m.SuggestedProjectId, m.SuggestedConfidence, m.Attachments.Count > 0))
            .ToListAsync();
        // shortcut: recipe name is the id until Task 4's RecipeCatalog exists.
        var jobs = await db.Jobs.Where(j => j.ProjectId == id).OrderByDescending(j => j.CreatedAt)
            .Select(j => new JobDto(j.Id, j.ProjectId, project.JobNo, j.RecipeId, j.RecipeId, j.Title, j.Engine, j.Status, j.CreatedAt, j.UpdatedAt,
                db.JobRuns.Where(r => r.JobId == j.Id).Sum(r => r.CostUsd), j.Result == null ? null : j.Result.Summary))
            .ToListAsync();
        var artifacts = await db.Artifacts.Where(a => a.ProjectId == id).OrderBy(a => a.RelativePath)
            .Select(a => new ArtifactDto(a.Id, a.ProjectId, a.JobId, a.RelativePath, a.DocCode, a.Version, a.Status, a.Size, a.ModifiedAt))
            .ToListAsync();
        var activity = await db.Activities.Where(a => a.ProjectId == id).OrderByDescending(a => a.Id)
            .Select(a => new ActivityDto(a.Id, a.At, a.Kind, a.Text, a.ProjectId, a.JobId))
            .ToListAsync();
        return new(project, mails, jobs, artifacts, activity);
    }

    static IResult Open(OfnrOptions o, ILogger log, string? path)
    {
        string full;
        try { full = Path.GetFullPath(path!); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Error(400, "열 수 없는 경로입니다."); }
        if (!new[] { o.ProjectsRoot, o.JobsRoot, o.TemplatesDir }.Any(root => IsUnder(full, root)))
            return Error(400, "프로젝트, 작업, 양식 폴더 안의 파일만 열 수 있습니다.");
        var isDir = Directory.Exists(full);
        // A ':' in the name would address an NTFS alternate stream such as run.exe:x.pdf.
        if (!isDir && (!OpenableExtensions.Contains(Path.GetExtension(full)) || Path.GetFileName(full).Contains(':')))
            return Error(400, "열 수 없는 파일 형식입니다.");
        if (!isDir && !File.Exists(full)) return Error(404, "파일을 찾을 수 없습니다.");
        if (!OperatingSystem.IsWindows())
        {
            log.LogInformation("Open skipped on this OS: {Path}", full);
            return Results.NoContent();
        }
        // Full path: a bare explorer.exe is searched in the app and current directories first.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(isDir ? new ProcessStartInfo(explorer) { ArgumentList = { full } } : new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { return Error(400, "이 파일을 열 프로그램이 없습니다. 프로그램을 설치한 뒤 다시 열어 주세요."); }
        return Results.NoContent();
    }

    static bool IsUnder(string full, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(r, cmp) || full.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }

    static void Apply(Client c, ClientInput i)
    {
        c.Name = i.Name!.Trim();
        c.BizRegNo = i.BizRegNo;
        c.ContactName = i.ContactName;
        c.ContactEmail = i.ContactEmail;
        c.ContactPhone = i.ContactPhone;
        c.MailDomains = (i.MailDomains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToArray();
        c.Notes = i.Notes;
    }

    static void Apply(Project p, ProjectInput i)
    {
        p.Name = i.Name!.Trim();
        p.ClientId = i.ClientId;
        p.Venue = i.Venue;
        p.InstallDate = i.InstallDate;
        p.OpenDate = i.OpenDate;
        p.EndDate = i.EndDate;
        p.TeardownDate = i.TeardownDate;
        p.QuoteAmount = i.QuoteAmount;
        p.ContractAmount = i.ContractAmount;
        p.CostAmount = i.CostAmount;
        p.Instructions = i.Instructions;
    }

    static IResult ProjectNotFound() => Error(404, "프로젝트를 찾을 수 없습니다.");

    static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
