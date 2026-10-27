import type { ArtifactStatus, Engine, JobStatus, MailStatus, Stage } from "./types";

const STAGE_LABELS: Record<Stage, string> = {
  Inquiry: "문의",
  Planning: "기획",
  Design: "디자인",
  Production: "제작",
  Operation: "운영",
  Report: "리포트",
  Done: "완료",
};

const krw = new Intl.NumberFormat("ko-KR");
const dateFormat = new Intl.DateTimeFormat("ko-KR", { year: "numeric", month: "2-digit", day: "2-digit" });

// Date-only strings ("2026-10-30") are calendar days, so parse them as local midnight, not UTC.
function toDate(value: string | Date): Date {
  if (value instanceof Date) return value;
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return m ? new Date(+m[1], +m[2] - 1, +m[3]) : new Date(value);
}

export function formatKrw(n: number): string {
  return `${krw.format(n)}원`;
}

export function formatDate(value: string | Date): string {
  return dateFormat.format(toDate(value));
}

/** Calendar days from today to the date; negative when it has passed. */
export function daysUntil(value: string | Date, today: Date = new Date()): number {
  const d = toDate(value);
  const target = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const base = new Date(today.getFullYear(), today.getMonth(), today.getDate());
  return Math.round((target.getTime() - base.getTime()) / 86_400_000);
}

export function dday(value: string | Date, today: Date = new Date()): string {
  const days = daysUntil(value, today);
  return days === 0 ? "D-day" : days > 0 ? `D-${days}` : `D+${-days}`;
}

export function stageLabel(stage: Stage): string {
  return STAGE_LABELS[stage];
}

const timeFormat = new Intl.DateTimeFormat("ko-KR", { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });

export function formatTime(value: string): string {
  return timeFormat.format(new Date(value));
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export function formatUsd(usd: number): string {
  return `$${usd.toFixed(2)}`;
}

export const JOB_STATUS: Record<JobStatus, string> = {
  Queued: "대기",
  Running: "실행 중",
  NeedsAttention: "확인 필요",
  Review: "검토",
  Done: "완료",
  Canceled: "취소",
  Failed: "실패",
};
export const MAIL_STATUS: Record<MailStatus, string> = { New: "신규", Linked: "연결", NeedsReply: "답장 필요", Done: "완료" };
export const ARTIFACT_STATUS: Record<ArtifactStatus, string> = {
  Draft: "초안",
  InternalApproved: "내부승인",
  SentToClient: "고객발송",
  ClientApproved: "고객승인",
};
export const ENGINE: Record<Engine, string> = { claude: "Claude Code", codex: "Codex" };
export const DATE_KINDS = { install: "설치", open: "오픈", end: "종료", teardown: "철수" } as const;
const LOCKS: Record<string, string> = { office: "Office", sketchup: "SketchUp", illustrator: "Illustrator", photoshop: "Photoshop" };

export function lockLabel(name: string): string {
  return LOCKS[name] ?? name;
}
