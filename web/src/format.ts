import type { Stage } from "./types";

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

export function dday(value: string | Date, today: Date = new Date()): string {
  const d = toDate(value);
  const target = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const base = new Date(today.getFullYear(), today.getMonth(), today.getDate());
  const days = Math.round((target.getTime() - base.getTime()) / 86_400_000);
  return days === 0 ? "D-day" : days > 0 ? `D-${days}` : `D+${-days}`;
}

export function stageLabel(stage: Stage): string {
  return STAGE_LABELS[stage];
}
