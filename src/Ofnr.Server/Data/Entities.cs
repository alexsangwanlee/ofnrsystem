using System.Text.Json;

namespace Ofnr.Server.Data;

public enum ProjectStage { Inquiry, Planning, Design, Production, Operation, Report, Done }

public enum ProjectStatus { Active, OnHold, Lost, Completed }

public enum MailStatus { New, Linked, NeedsReply, Done }

public enum JobStatus { Queued, Running, NeedsAttention, Review, Done, Canceled, Failed }

public enum RunStatus { Queued, Working, Blocked, Done, Failed, Stopped }

public enum ArtifactStatus { Draft, InternalApproved, SentToClient, ClientApproved }

public enum ApprovalStatus { Pending, Decided, Expired }

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "owner"; // owner | staff
}

public sealed class Client
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string? BizRegNo { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string[] MailDomains { get; set; } = [];
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string JobNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }
    public ProjectStage Stage { get; set; } = ProjectStage.Inquiry;
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public string? Venue { get; set; }
    public DateOnly? InstallDate { get; set; }
    public DateOnly? OpenDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? TeardownDate { get; set; }
    public long? QuoteAmount { get; set; }
    public long? ContractAmount { get; set; }
    public long? CostAmount { get; set; }
    public string FolderPath { get; set; } = "";
    public string? Instructions { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Folder { get; set; } = "";
    // IMAP UIDs are uint32; long because Npgsql maps uint to xid.
    public long UidValidity { get; set; }
    public long Uid { get; set; }
    public bool Outgoing { get; set; }
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string[] References { get; set; } = [];
    public string ThreadKey { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string To { get; set; } = "";
    public string? Cc { get; set; }
    public string Subject { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public List<MailAttachment> Attachments { get; set; } = [];
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public MailStatus Status { get; set; } = MailStatus.New;
    public string? Category { get; set; }
    public string? Summary { get; set; }
    public JsonDocument? Extracted { get; set; }
    public Guid? SuggestedProjectId { get; set; }
    public double? SuggestedConfidence { get; set; }
}

public sealed class MailAttachment
{
    public int Index { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
}

public sealed class MailDraft
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ReplyToMailId { get; set; }
    public MailMessage? ReplyToMail { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string To { get; set; } = "";
    public string? Cc { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string[] Attachments { get; set; } = [];
    public string Author { get; set; } = "owner"; // owner | ai
    public string Status { get; set; } = "draft"; // draft | sent
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class MailSyncState
{
    public string Folder { get; set; } = "";
    public long UidValidity { get; set; }
    public long LastUid { get; set; }
}

public sealed class Job
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string RecipeId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instruction { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string Engine { get; set; } = "claude"; // claude | codex
    public string Source { get; set; } = "manual"; // mail | stage | manual
    public Guid? MailId { get; set; }
    public MailMessage? Mail { get; set; }
    public string? EngineSession { get; set; }
    public JobResult? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobResult
{
    public string Summary { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> NeedsConfirmation { get; set; } = [];
    public List<string> NextSteps { get; set; } = [];
}

public sealed class JobRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public int Seq { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? SessionId { get; set; }
    public string? EngineVersion { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public decimal? CostUsd { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string? Error { get; set; }
}

public sealed class JobEvent
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public Guid RunId { get; set; }
    public JobRun Run { get; set; } = null!;
    public int Seq { get; set; }
    public string Kind { get; set; } = ""; // text | tool | usage | warning | error | system | result
    public string Summary { get; set; } = "";
    public string? Raw { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Approval
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public Guid? RunId { get; set; }
    public JobRun? Run { get; set; }
    public string Kind { get; set; } = "tool"; // tool | question | deliverable
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public string? Decision { get; set; } // accept | edit | respond | ignore
    public string? ResponseText { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
}

public sealed class Artifact
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? JobId { get; set; }
    public Job? Job { get; set; }
    public string RelativePath { get; set; } = "";
    public string? DocCode { get; set; }
    public int Version { get; set; }
    public ArtifactStatus Status { get; set; } = ArtifactStatus.Draft;
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Feedback
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public Job Job { get; set; } = null!;
    public string Text { get; set; } = "";
    public string? SkillName { get; set; }
    public Guid? LearningJobId { get; set; }
    public Job? LearningJob { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Activity
{
    public long Id { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? JobId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
