using Microsoft.EntityFrameworkCore;
using Ofnr.Server.Data;
using Ofnr.Server.Realtime;
using Ofnr.Server.Skills;
using static Ofnr.Server.Projects.ProjectEndpoints;

namespace Ofnr.Server.Jobs;

public static class JobEndpoints
{
    public static async Task<List<JobDto>> ToDtosAsync(this IQueryable<Job> q, OfnrDb db, RecipeCatalog recipes) =>
        (await q.Select(j => new JobDto(
                j.Id, j.ProjectId, j.Project != null ? j.Project.JobNo : null, j.RecipeId, j.RecipeId, j.Title, j.Engine, j.Status, j.CreatedAt, j.UpdatedAt,
                db.JobRuns.Where(r => r.JobId == j.Id).Sum(r => r.CostUsd), j.Result == null ? null : j.Result.Summary))
            .ToListAsync())
        .Select(d => d with { RecipeName = recipes.Get(d.RecipeId)?.Name ?? d.RecipeId })
        .ToList();

    public static async Task<JobDto> DtoAsync(OfnrDb db, RecipeCatalog recipes, Guid jobId) =>
        (await db.Jobs.Where(j => j.Id == jobId).ToDtosAsync(db, recipes)).Single();

    static string DefaultEngine(Recipe r, OfnrOptions o) => r.Engines.Contains(o.DefaultEngine) ? o.DefaultEngine : r.Engines[0];

    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recipes", (RecipeCatalog recipes) => recipes.All.Select(RecipeDto.From));

        app.MapGet("/api/jobs", (OfnrDb db, RecipeCatalog recipes, JobStatus? status, Guid? projectId) =>
            db.Jobs.Where(j => (status == null || j.Status == status) && (projectId == null || j.ProjectId == projectId))
                .OrderByDescending(j => j.CreatedAt).ToDtosAsync(db, recipes));

        app.MapPost("/api/jobs", async (OfnrDb db, SettingsStore settings, RecipeCatalog recipes, IJobQueue queue, IOpsNotifier ops, JobInput input) =>
        {
            if (recipes.Get(input.RecipeId ?? "") is not { } recipe) return Error(400, "알 수 없는 작업 종류입니다.");
            // A learning job edits the company skills, so only the CEO's review of a finished job may start one.
            if (recipe.Id == "skill-learning") return Error(400, "스킬 학습은 검토 의견에서만 시작할 수 있습니다.");
            var engine = input.Engine ?? DefaultEngine(recipe, settings.Current);
            if (!recipe.Engines.Contains(engine)) return Error(400, $"이 작업은 {engine}로 실행할 수 없습니다.");
            Project? project = null;
            if (input.ProjectId is { } projectId && (project = await db.Projects.FindAsync(projectId)) is null) return Error(404, "프로젝트를 찾을 수 없습니다.");
            if (recipe.NeedsProject && project is null) return Error(400, "프로젝트를 선택해 주세요.");
            if (input.MailId is { } mailId && !await db.MailMessages.AnyAsync(m => m.Id == mailId)) return Error(404, "메일을 찾을 수 없습니다.");

            var job = new Job
            {
                ProjectId = project?.Id, RecipeId = recipe.Id, Title = project is null ? recipe.Name : $"{recipe.Name} · {project.Name}",
                Instruction = input.Instruction?.Trim() ?? "", Engine = engine, Source = input.MailId is null ? "manual" : "mail", MailId = input.MailId,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return Results.Ok(await QueuedAsync(db, recipes, queue, ops, job.Id));
        });

        app.MapGet("/api/jobs/{id:guid}", async (OfnrDb db, RecipeCatalog recipes, Guid id) =>
        {
            if (await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id) is not { } job) return JobNotFound();
            var runs = await db.JobRuns.Where(r => r.JobId == id).OrderBy(r => r.Seq)
                .Select(r => new RunDto(r.Id, r.Seq, r.Status, r.EngineVersion, r.StartedAt, r.EndedAt, r.CostUsd, r.Error)).ToListAsync();
            var events = await db.JobEvents.Where(e => e.JobId == id).OrderBy(e => e.Run.Seq).ThenBy(e => e.Seq)
                .Select(e => new JobEventDto(e.Id, e.JobId, e.RunId, e.Seq, e.Kind, e.Summary, e.At)).ToListAsync();
            var approvals = (await db.Approvals.Where(a => a.JobId == id).OrderBy(a => a.CreatedAt).ToListAsync()).Select(ApprovalDto.From).ToList();
            var artifacts = await db.Artifacts.Where(a => a.JobId == id).OrderBy(a => a.RelativePath)
                .Select(a => new ArtifactDto(a.Id, a.ProjectId, a.JobId, a.RelativePath, a.DocCode, a.Version, a.Status, a.Size, a.ModifiedAt)).ToListAsync();
            var feedback = (await db.Feedback.Where(f => f.JobId == id).OrderBy(f => f.CreatedAt).ToListAsync()).Select(FeedbackDto.From).ToList();
            return Results.Ok(new JobDetailDto(await DtoAsync(db, recipes, id), runs, events, approvals, artifacts, feedback, job.Result));
        });

        app.MapPost("/api/jobs/{id:guid}/cancel", async (OfnrDb db, RecipeCatalog recipes, IJobQueue queue, Guid id) =>
        {
            if (await db.Jobs.Where(j => j.Id == id).Select(j => (JobStatus?)j.Status).SingleOrDefaultAsync() is not { } status) return JobNotFound();
            if (status is not (JobStatus.Queued or JobStatus.Running or JobStatus.NeedsAttention)) return Error(409, "진행 중인 작업이 아닙니다.");
            await queue.CancelAsync(id);
            return Results.Ok(await DtoAsync(db, recipes, id));
        });

        app.MapPost("/api/jobs/{id:guid}/followup", async (OfnrDb db, RecipeCatalog recipes, IJobQueue queue, Guid id, FollowupInput input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Instruction)) return Error(400, "지시 내용을 입력해 주세요.");
            if (!await db.Jobs.AnyAsync(j => j.Id == id)) return JobNotFound();
            await queue.FollowupAsync(id, input.Instruction.Trim());
            return Results.Ok(await DtoAsync(db, recipes, id));
        });

        app.MapPost("/api/jobs/{id:guid}/complete", async (OfnrDb db, RecipeCatalog recipes, IOpsNotifier ops, Guid id) =>
        {
            if (await db.Jobs.FindAsync(id) is not { } job) return JobNotFound();
            if (job.Status is JobStatus.Queued or JobStatus.Running) return Error(409, "실행 중인 작업은 완료할 수 없습니다.");
            (job.Status, job.UpdatedAt) = (JobStatus.Done, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await db.Artifacts.Where(a => a.JobId == id && a.Status == ArtifactStatus.Draft)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ArtifactStatus.InternalApproved));
            await Approvals.ExpirePendingAsync(db, id);
            var dto = await DtoAsync(db, recipes, id);
            await ops.JobUpdated(dto);
            return Results.Ok(dto);
        });

        app.MapPost("/api/jobs/{id:guid}/feedback", async (OfnrDb db, SettingsStore settings, RecipeCatalog recipes, IJobQueue queue, IOpsNotifier ops, Guid id, FeedbackInput input) =>
        {
            if (await db.Jobs.Include(j => j.Project).SingleOrDefaultAsync(j => j.Id == id) is not { } source) return JobNotFound();
            if (string.IsNullOrWhiteSpace(input.Text)) return Error(400, "검토 의견을 입력해 주세요.");
            var o = settings.Current;
            var skill = string.IsNullOrWhiteSpace(input.SkillName) ? null : input.SkillName.Trim();
            // The absolute path: Codex works on copies of the recipe's skills and would otherwise edit a copy.
            var skillFile = skill is null ? null : Path.Combine(o.PluginDir, "skills", skill, "SKILL.md");
            if (skill is not null && (!SkillStore.ValidName().IsMatch(skill) || !File.Exists(skillFile))) return Error(400, "알 수 없는 스킬입니다.");

            var feedback = new Feedback { JobId = id, Text = input.Text.Trim(), SkillName = skill };
            db.Feedback.Add(feedback);
            Job? learning = null;
            if (skill is not null)
            {
                var recipe = recipes.Get("skill-learning");
                learning = new Job
                {
                    RecipeId = "skill-learning", Title = $"스킬 학습 · {skill}", Engine = recipe is null ? o.DefaultEngine : DefaultEngine(recipe, o),
                    Instruction = $"""
                        대상 스킬: {skill}
                        스킬 파일: {skillFile} (복사본이 아니라 이 파일을 직접 고치세요.)
                        출처 작업번호: {source.Project?.JobNo ?? "없음"}
                        날짜: {DateTimeOffset.Now:yyyy-MM-dd}
                        검토 의견:
                        {feedback.Text}
                        """,
                };
                db.Jobs.Add(learning);
                feedback.LearningJobId = learning.Id;
            }
            await db.SaveChangesAsync();
            if (learning is not null) await QueuedAsync(db, recipes, queue, ops, learning.Id);
            return Results.Ok(FeedbackDto.From(feedback));
        });

        app.MapPost("/api/approvals/{id:guid}/decide", async (Approvals approvals, IJobQueue queue, Guid id, DecisionInput input) =>
        {
            ApprovalDto dto;
            try { dto = await approvals.DecideAsync(id, input.Decision ?? "", input.Text); }
            catch (ArgumentException e) { return Error(400, e.Message); }
            catch (KeyNotFoundException e) { return Error(404, e.Message); }
            catch (InvalidOperationException e) { return Error(409, e.Message); }
            // The answer continues the same engine session; an option the CEO picked arrives as the text.
            if (dto.Decision != "ignore") await queue.FollowupAsync(dto.JobId, $"대표 답변: {(string.IsNullOrWhiteSpace(input.Text) ? "승인" : input.Text.Trim())}");
            return Results.Ok(dto);
        });
    }

    /// <summary>Announces a job just saved as queued and wakes the queue.</summary>
    internal static async Task<JobDto> QueuedAsync(OfnrDb db, RecipeCatalog recipes, IJobQueue queue, IOpsNotifier ops, Guid jobId)
    {
        var dto = await DtoAsync(db, recipes, jobId);
        await ops.JobUpdated(dto);
        await queue.EnqueueAsync(jobId);
        return dto;
    }

    static IResult JobNotFound() => Error(404, "작업을 찾을 수 없습니다.");
}
