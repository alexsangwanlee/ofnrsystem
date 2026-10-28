using System.Text.Json;
using Ofnr.Server.Data;
using Ofnr.Server.Jobs;
using Ofnr.Server.Ops;

namespace Ofnr.Server;

// API Contract DTOs shared by the endpoints and the SignalR hub. Enums serialize by name.

public sealed record ClientInput(
    string? Name, string? BizRegNo, string? ContactName, string? ContactEmail, string? ContactPhone, string[]? MailDomains, string? Notes);

public sealed record ClientDto(
    Guid Id, string Name, string? BizRegNo, string? ContactName, string? ContactEmail, string? ContactPhone, string[] MailDomains, string? Notes)
{
    public static ClientDto From(Client c) => new(c.Id, c.Name, c.BizRegNo, c.ContactName, c.ContactEmail, c.ContactPhone, c.MailDomains, c.Notes);
}

/// <param name="Status">Null keeps the project's status (a new project starts Active).</param>
public sealed record ProjectInput(
    string? Name, string? Slug, Guid? ClientId, ProjectStage? Stage, string? Venue,
    DateOnly? InstallDate, DateOnly? OpenDate, DateOnly? EndDate, DateOnly? TeardownDate,
    long? QuoteAmount, long? ContractAmount, long? CostAmount, string? Instructions, ProjectStatus? Status = null);

public sealed record ProjectDto(
    Guid Id, string JobNo, string Name, string Slug, Guid? ClientId, string? ClientName, ProjectStage Stage, ProjectStatus Status, string? Venue,
    DateOnly? InstallDate, DateOnly? OpenDate, DateOnly? EndDate, DateOnly? TeardownDate,
    long? QuoteAmount, long? ContractAmount, long? CostAmount, string FolderPath, int AttentionCount, int RunningCount, string? Instructions);

public sealed record ProjectDetailDto(ProjectDto Project, List<MailSummaryDto> Mails, List<JobDto> Jobs, List<ArtifactDto> Artifacts, List<ActivityDto> Activity);

public sealed record StageInput(ProjectStage? Stage);

public sealed record StageMoveDto(ProjectDto Project, RecipeDto[] Suggestions, string[] Warnings);

public sealed record FileDto(string RelativePath, long Size, DateTimeOffset ModifiedAt, Guid? ArtifactId, ArtifactStatus? Status);

public sealed record OpenInput(string? Path);

public sealed record RecipeDto(string Id, string Name, ProjectStage? Stage, string[] Engines, string? Lock, bool NeedsProject, string[] Skills)
{
    public static RecipeDto From(Recipe r) => new(r.Id, r.Name, r.Stage, r.Engines, r.Lock, r.NeedsProject, r.Skills);
}

public sealed record ArtifactDto(
    Guid Id, Guid ProjectId, Guid? JobId, string RelativePath, string? DocCode, int Version, ArtifactStatus Status, long Size, DateTimeOffset ModifiedAt);

public sealed record ActivityDto(long Id, DateTimeOffset At, string Kind, string Summary, Guid? ProjectId, Guid? JobId);

public sealed record JobDto(
    Guid Id, Guid? ProjectId, string? JobNo, string RecipeId, string RecipeName, string Title, string Engine, JobStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, decimal? CostUsd, string? Summary);

public sealed record JobEventDto(long Id, Guid JobId, Guid RunId, int Seq, string Kind, string Summary, DateTimeOffset At);

public sealed record JobInput(string? RecipeId, Guid? ProjectId, string? Instruction, string? Engine, Guid? MailId);

public sealed record FollowupInput(string? Instruction);

public sealed record FeedbackInput(string? Text, string? SkillName);

public sealed record DecisionInput(string? Decision, string? Text);

public sealed record RunDto(Guid Id, int Seq, RunStatus Status, string? EngineVersion, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, decimal? CostUsd, string? Error);

public sealed record FeedbackDto(Guid Id, Guid JobId, string Text, string? SkillName, Guid? LearningJobId, DateTimeOffset CreatedAt)
{
    public static FeedbackDto From(Feedback f) => new(f.Id, f.JobId, f.Text, f.SkillName, f.LearningJobId, f.CreatedAt);
}

/// <param name="Result">The API Contract's JobResultDto, which has the entity's shape.</param>
public sealed record JobDetailDto(
    JobDto Job, List<RunDto> Runs, List<JobEventDto> Events, List<ApprovalDto> Approvals, List<ArtifactDto> Artifacts, List<FeedbackDto> Feedback, JobResult? Result);

public sealed record LockDto(string Name, Guid? HolderJobId, int Waiting);

public sealed record ApprovalDto(Guid Id, Guid JobId, string Kind, string Title, string Detail, ApprovalStatus Status, string? Decision, DateTimeOffset CreatedAt)
{
    public static ApprovalDto From(Approval a) => new(a.Id, a.JobId, a.Kind, a.Title, a.Detail, a.Status, a.Decision, a.CreatedAt);
}

public sealed record MailSummaryDto(
    Guid Id, string FromName, string FromAddress, string Subject, DateTimeOffset ReceivedAt, MailStatus Status, string? Category, string? Summary,
    Guid? ProjectId, string? JobNo, Guid? SuggestedProjectId, double? SuggestedConfidence, bool HasAttachments, bool HasDraft);

/// <param name="Extracted">The triage fields (Task 8), an empty object before triage.</param>
public sealed record MailDetailDto(
    Guid Id, string FromName, string FromAddress, string Subject, DateTimeOffset ReceivedAt, MailStatus Status, string? Category, string? Summary,
    Guid? ProjectId, string? JobNo, Guid? SuggestedProjectId, double? SuggestedConfidence, bool HasAttachments, bool HasDraft,
    string To, string? Cc, string? ReplyTo, string? TextBody, string? HtmlBody, List<MailAttachment> Attachments, JsonElement Extracted, List<MailSummaryDto> Thread);

public sealed record MailLinkInput(Guid? ProjectId);

public sealed record MailStatusInput(MailStatus? Status);

public sealed record DraftInput(Guid? ReplyToMailId, Guid? ProjectId, string? To, string? Cc, string? Subject, string? Body, string[]? Attachments);

public sealed record MailDraftDto(
    Guid Id, Guid? ReplyToMailId, Guid? ProjectId, string To, string? Cc, string Subject, string Body, string[] Attachments, string Author, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? SentAt)
{
    public static MailDraftDto From(MailDraft d) => new(d.Id, d.ReplyToMailId, d.ProjectId, d.To, d.Cc, d.Subject, d.Body, d.Attachments, d.Author, d.Status, d.CreatedAt, d.SentAt);
}

/// <param name="Content">The whole SKILL.md; null in the list.</param>
public sealed record SkillDto(string Name, string Description, string[] LearnedRules, string? Content);

public sealed record SkillInput(string? Content);

public sealed record SkillHistoryDto(string Commit, DateTimeOffset Date, string Message);

public sealed record RevertInput(string? Commit);

/// <summary>The settings without the connection string (passwords live in SecretStore).</summary>
public sealed record SettingsDto(
    string DataRoot, string ProjectsRoot, string JobsRoot, string PluginDir, string TemplatesDir, string BackupsDir, int Port,
    string ClaudeExe, string CodexExe, string DefaultEngine, int MaxConcurrentJobs, int MailSyncMinutes, bool AutoTriage, bool AppendSentCopy,
    string? ClaudeModel, string? CodexModel, string PgDumpPath, MailAccount Mail)
{
    public static SettingsDto From(OfnrOptions o) => new(
        o.DataRoot, o.ProjectsRoot, o.JobsRoot, o.PluginDir, o.TemplatesDir, o.BackupsDir, o.Port, o.ClaudeExe, o.CodexExe, o.DefaultEngine,
        o.MaxConcurrentJobs, o.MailSyncMinutes, o.AutoTriage, o.AppendSentCopy, o.ClaudeModel, o.CodexModel, o.PgDumpPath, o.Mail);
}

public sealed record PasswordInput(string? Password);

/// <param name="Kind">approval, review, failed or blocked.</param>
/// <param name="ApprovalKind">The approval's kind when <paramref name="Kind"/> is approval; question comes from ask_ceo.</param>
public sealed record AttentionDto(string Kind, Guid JobId, string Title, string? Detail, Guid? ApprovalId, string? ApprovalKind);

/// <param name="Kind">install, open, end or teardown.</param>
public sealed record UpcomingDto(Guid ProjectId, string JobNo, string ProjectName, string Kind, DateOnly Date);

public sealed record TodayCounts(int Attention, int Running, int ThisWeek);

public sealed record TodayDto(TodayCounts Counts, List<AttentionDto> Attention, List<MailSummaryDto> UnsortedMail, List<UpcomingDto> Upcoming, List<JobDto> Running);

public sealed record RecipeCostDto(string RecipeId, decimal Usd, int Runs);

public sealed record CostsDto(decimal MonthUsd, List<RecipeCostDto> ByRecipe);

public sealed record AgentsDto(List<JobDto> Running, List<JobDto> Queued, IReadOnlyList<LockDto> Locks, CostsDto Costs, EngineDto[] Engines);
