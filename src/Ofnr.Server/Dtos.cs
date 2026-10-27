using Ofnr.Server.Data;

namespace Ofnr.Server;

// API Contract DTOs shared by the endpoints and the SignalR hub. Enums serialize by name.

public sealed record ClientInput(
    string? Name, string? BizRegNo, string? ContactName, string? ContactEmail, string? ContactPhone, string[]? MailDomains, string? Notes);

public sealed record ClientDto(
    Guid Id, string Name, string? BizRegNo, string? ContactName, string? ContactEmail, string? ContactPhone, string[] MailDomains, string? Notes)
{
    public static ClientDto From(Client c) => new(c.Id, c.Name, c.BizRegNo, c.ContactName, c.ContactEmail, c.ContactPhone, c.MailDomains, c.Notes);
}

public sealed record ProjectInput(
    string? Name, string? Slug, Guid? ClientId, ProjectStage? Stage, string? Venue,
    DateOnly? InstallDate, DateOnly? OpenDate, DateOnly? EndDate, DateOnly? TeardownDate,
    long? QuoteAmount, long? ContractAmount, long? CostAmount, string? Instructions);

public sealed record ProjectDto(
    Guid Id, string JobNo, string Name, string Slug, Guid? ClientId, string? ClientName, ProjectStage Stage, ProjectStatus Status, string? Venue,
    DateOnly? InstallDate, DateOnly? OpenDate, DateOnly? EndDate, DateOnly? TeardownDate,
    long? QuoteAmount, long? ContractAmount, long? CostAmount, string FolderPath, int AttentionCount, int RunningCount);

public sealed record ProjectDetailDto(ProjectDto Project, List<MailSummaryDto> Mails, List<JobDto> Jobs, List<ArtifactDto> Artifacts, List<ActivityDto> Activity);

public sealed record StageInput(ProjectStage? Stage);

public sealed record StageMoveDto(ProjectDto Project, RecipeDto[] Suggestions, string[] Warnings);

public sealed record FileDto(string RelativePath, long Size, DateTimeOffset ModifiedAt, Guid? ArtifactId, ArtifactStatus? Status);

public sealed record OpenInput(string? Path);

public sealed record RecipeDto(string Id, string Name, ProjectStage? Stage, string[] Engines, string? Lock, bool NeedsProject);

public sealed record ArtifactDto(
    Guid Id, Guid ProjectId, Guid? JobId, string RelativePath, string? DocCode, int Version, ArtifactStatus Status, long Size, DateTimeOffset ModifiedAt);

public sealed record ActivityDto(long Id, DateTimeOffset At, string Kind, string Summary, Guid? ProjectId, Guid? JobId);

public sealed record JobDto(
    Guid Id, Guid? ProjectId, string? JobNo, string RecipeId, string RecipeName, string Title, string Engine, JobStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, decimal? CostUsd, string? Summary);

public sealed record JobEventDto(long Id, Guid JobId, Guid RunId, int Seq, string Kind, string Summary, DateTimeOffset At);

public sealed record ApprovalDto(Guid Id, Guid JobId, string Kind, string Title, string Detail, ApprovalStatus Status, string? Decision, DateTimeOffset CreatedAt);

public sealed record MailSummaryDto(
    Guid Id, string FromName, string FromAddress, string Subject, DateTimeOffset ReceivedAt, MailStatus Status, string? Category, string? Summary,
    Guid? ProjectId, string? JobNo, Guid? SuggestedProjectId, double? SuggestedConfidence, bool HasAttachments);
