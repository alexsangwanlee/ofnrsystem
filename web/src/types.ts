// DTOs from the plan's API Contract. Server enums are serialized by name (e.g. "Inquiry").

export const STAGES = ["Inquiry", "Planning", "Design", "Production", "Operation", "Report", "Done"] as const;
export type Stage = (typeof STAGES)[number];
export type ProjectStatus = "Active" | "OnHold" | "Lost" | "Completed";
export type MailStatus = "New" | "Linked" | "NeedsReply" | "Done";
export type JobStatus = "Queued" | "Running" | "NeedsAttention" | "Review" | "Done" | "Canceled" | "Failed";
export type RunStatus = "Queued" | "Working" | "Blocked" | "Done" | "Failed" | "Stopped";
export type ArtifactStatus = "Draft" | "InternalApproved" | "SentToClient" | "ClientApproved";
export type ApprovalStatus = "Pending" | "Decided" | "Expired";
export type Engine = "claude" | "codex";
export type Decision = "accept" | "edit" | "respond" | "ignore";

export interface ApiErrorBody { error: string }

export interface CheckDto { name: string; ok: boolean; detail: string | null }
export interface EngineDto {
  /** Also the {engine} segment of POST /api/health/engine-check/{engine}. */
  name: Engine;
  path: string;
  version: string | null;
  /** Pinned version, e.g. "2.1.311". A different `version` is a warning. */
  pinned: string;
  ok: boolean;
  loggedIn: boolean;
  detail: string | null;
}
export interface HealthDto {
  version: string;
  dataRoot: string;
  projectsRoot: string;
  db: CheckDto;
  mail: CheckDto;
  engines: EngineDto[];
  mcp: CheckDto[];
  paths: CheckDto[];
}

export interface ClientInput {
  name: string;
  bizRegNo?: string | null;
  contactName?: string | null;
  contactEmail?: string | null;
  contactPhone?: string | null;
  mailDomains: string[];
  notes?: string | null;
}
export interface ClientDto extends ClientInput { id: string }

export interface ProjectInput {
  name: string;
  slug?: string | null;
  clientId?: string | null;
  stage?: Stage | null;
  venue?: string | null;
  installDate?: string | null;
  openDate?: string | null;
  endDate?: string | null;
  teardownDate?: string | null;
  quoteAmount?: number | null;
  contractAmount?: number | null;
  costAmount?: number | null;
  instructions?: string | null;
  /** Null keeps the project's status; a new project starts Active. */
  status?: ProjectStatus | null;
}
export interface ProjectDto {
  id: string;
  jobNo: string;
  name: string;
  slug: string;
  clientId: string | null;
  clientName: string | null;
  stage: Stage;
  status: ProjectStatus;
  venue: string | null;
  installDate: string | null;
  openDate: string | null;
  endDate: string | null;
  teardownDate: string | null;
  quoteAmount: number | null;
  contractAmount: number | null;
  costAmount: number | null;
  folderPath: string;
  attentionCount: number;
  runningCount: number;
  instructions: string | null;
}
export interface ActivityDto {
  id: number;
  at: string;
  kind: string;
  summary: string;
  projectId: string | null;
  jobId: string | null;
}
export interface ProjectDetailDto {
  project: ProjectDto;
  mails: MailSummaryDto[];
  jobs: JobDto[];
  artifacts: ArtifactDto[];
  activity: ActivityDto[];
}
export interface StageMoveResult { project: ProjectDto; suggestions: RecipeDto[]; warnings: string[] }
export interface FileDto {
  relativePath: string;
  size: number;
  modifiedAt: string;
  artifactId?: string | null;
  status?: ArtifactStatus | null;
}

export interface MailSummaryDto {
  id: string;
  fromName: string | null;
  fromAddress: string;
  subject: string;
  receivedAt: string;
  status: MailStatus;
  category: string | null;
  summary: string | null;
  projectId: string | null;
  jobNo: string | null;
  suggestedProjectId: string | null;
  suggestedConfidence: number | null;
  hasAttachments: boolean;
  hasDraft: boolean;
}
export interface MailAttachmentDto { index: number; fileName: string; size: number }
export interface MailDetailDto extends MailSummaryDto {
  to: string;
  cc: string | null;
  textBody: string | null;
  htmlBody: string | null;
  attachments: MailAttachmentDto[];
  extracted: Record<string, string | string[] | null>;
  thread: MailSummaryDto[];
}
export interface DraftInput {
  replyToMailId?: string | null;
  projectId?: string | null;
  to: string;
  cc?: string | null;
  subject: string;
  body: string;
  attachments: string[];
}
export interface MailDraftDto extends DraftInput {
  id: string;
  /** "owner" (the CEO) or "ai" */
  author: string;
  status: "draft" | "sent";
  createdAt: string;
  sentAt: string | null;
}

export interface RecipeDto {
  id: string;
  name: string;
  stage: Stage | null;
  engines: Engine[];
  lock: string | null;
  needsProject: boolean;
  /** Skill folder names under claude-plugin/skills. */
  skills: string[];
}

export interface JobInput {
  recipeId: string;
  projectId?: string | null;
  instruction: string;
  engine?: Engine | null;
  mailId?: string | null;
}
export interface JobDto {
  id: string;
  projectId: string | null;
  jobNo: string | null;
  recipeId: string;
  recipeName: string;
  title: string;
  engine: Engine;
  status: JobStatus;
  createdAt: string;
  updatedAt: string;
  costUsd: number | null;
  summary: string | null;
}
export interface RunDto {
  id: string;
  seq: number;
  status: RunStatus;
  engineVersion: string | null;
  startedAt: string | null;
  endedAt: string | null;
  costUsd: number | null;
  error: string | null;
}
export type JobEventKind = "text" | "tool" | "usage" | "warning" | "error" | "system" | "result";
export interface JobEventDto {
  id: number;
  jobId: string;
  runId: string;
  seq: number;
  kind: JobEventKind;
  summary: string;
  at: string;
}
export interface ApprovalDto {
  id: string;
  jobId: string;
  kind: string;
  title: string;
  detail: string;
  status: ApprovalStatus;
  decision: Decision | null;
  createdAt: string;
}
export interface DecisionInput { decision: Decision; text?: string | null }
export interface ArtifactDto {
  id: string;
  projectId: string;
  jobId: string | null;
  relativePath: string;
  docCode: string | null;
  version: number | null;
  status: ArtifactStatus;
  size: number;
  modifiedAt: string;
}
export interface FeedbackDto {
  id: string;
  jobId: string;
  text: string;
  skillName: string | null;
  learningJobId: string | null;
  createdAt: string;
}
export interface JobResultDto { summary: string; files: string[]; needsConfirmation: string[]; nextSteps: string[] }
export interface JobDetailDto {
  job: JobDto;
  runs: RunDto[];
  events: JobEventDto[];
  approvals: ApprovalDto[];
  artifacts: ArtifactDto[];
  feedback: FeedbackDto[];
  result?: JobResultDto | null;
}

export interface AttentionDto {
  kind: "approval" | "review" | "failed" | "blocked";
  jobId: string;
  title: string;
  detail: string | null;
  approvalId?: string | null;
  /** `ApprovalDto.kind` when `kind` is "approval"; "question" comes from ask_ceo. */
  approvalKind?: string | null;
}
export interface UpcomingDto {
  projectId: string;
  jobNo: string;
  projectName: string;
  kind: "install" | "open" | "end" | "teardown";
  date: string;
}
export interface TodayDto {
  counts: { attention: number; running: number; thisWeek: number };
  attention: AttentionDto[];
  unsortedMail: MailSummaryDto[];
  upcoming: UpcomingDto[];
  running: JobDto[];
}

export interface LockDto { name: string; holderJobId: string | null; waiting: number }
export interface AgentsDto {
  running: JobDto[];
  queued: JobDto[];
  locks: LockDto[];
  costs: { monthUsd: number; byRecipe: { recipeId: string; usd: number; runs: number }[] };
  engines: EngineDto[];
}

export interface SkillDto { name: string; description: string; learnedRules: string[]; content?: string | null }
export interface SkillHistoryDto { commit: string; date: string; message: string }

export interface MailAccountDto {
  address: string;
  imapHost: string;
  imapPort: number;
  smtpHost: string;
  smtpPort: number;
  sentFolder: string | null;
}
/** OfnrOptions without the connection string and secrets. */
export interface SettingsDto {
  dataRoot: string;
  projectsRoot: string;
  jobsRoot: string;
  pluginDir: string;
  templatesDir: string;
  backupsDir: string;
  port: number;
  claudeExe: string;
  codexExe: string;
  defaultEngine: Engine;
  maxConcurrentJobs: number;
  mailSyncMinutes: number;
  autoTriage: boolean;
  appendSentCopy: boolean;
  claudeModel: string | null;
  codexModel: string | null;
  pgDumpPath: string;
  mail: MailAccountDto;
}

export interface NotifyDto { title: string; body: string; link: string | null }
