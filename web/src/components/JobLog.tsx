import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { formatTime, toolLabel } from "../format";
import { joinJob, leaveJob, useOps } from "../realtime";
import type { JobEventDto } from "../types";

const useStyles = makeStyles({
  log: {
    maxHeight: "480px",
    overflowY: "auto",
    padding: tokens.spacingHorizontalM,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  line: { display: "flex", gap: tokens.spacingHorizontalS, whiteSpace: "pre-wrap", overflowWrap: "anywhere" },
  time: { flexShrink: 0, color: tokens.colorNeutralForeground4, fontVariantNumeric: "tabular-nums" },
  muted: { color: tokens.colorNeutralForeground3 },
  warning: { color: tokens.colorStatusWarningForeground1 },
  error: { color: tokens.colorStatusDangerForeground1, fontWeight: tokens.fontWeightSemibold },
  tool: {
    color: tokens.colorNeutralForeground3,
    "& > summary": { cursor: "pointer", listStylePosition: "inside", overflowWrap: "anywhere" },
  },
  args: {
    margin: `${tokens.spacingVerticalXS} 0 0 ${tokens.spacingHorizontalXXL}`,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    whiteSpace: "pre-wrap",
    overflowWrap: "anywhere",
  },
});

/** "Read src/a.ts" shows as "파일 읽기 src/a.ts"; the raw summary stays in the expanded details. */
const toolLine = (summary: string) => {
  const name = /^[^\s:(]+/.exec(summary)?.[0] ?? summary;
  return toolLabel(name) + summary.slice(name.length);
};

/** Job events: history from the job detail plus live `jobEvent`s, with tool calls collapsed. */
export function JobLog({ jobId, events }: { jobId: string; events: JobEventDto[] }) {
  const s = useStyles();
  const [live, setLive] = useState<JobEventDto[]>([]);
  const box = useRef<HTMLDivElement>(null);
  const stick = useRef(true);

  useEffect(() => {
    joinJob(jobId).catch(() => undefined);
    return () => void leaveJob(jobId).catch(() => undefined);
  }, [jobId]);
  useOps({
    jobEvent: (e) => {
      if (e.jobId === jobId) setLive((l) => [...l, e]);
    },
  });

  // History and live events overlap after a reload or reconnect; seq is unique within a run.
  const all = useMemo(() => {
    const bySeq = new Map<string, JobEventDto>();
    for (const e of [...events, ...live]) bySeq.set(`${e.runId}:${e.seq}`, e);
    return [...bySeq.values()].sort((a, b) => a.id - b.id);
  }, [events, live]);

  useLayoutEffect(() => {
    if (stick.current && box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [all]);

  return (
    <div
      ref={box}
      className={s.log}
      role="log"
      aria-label="실행 로그"
      onScroll={(e) => {
        const el = e.currentTarget;
        stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
      }}
    >
      {all.length === 0 && <span className={s.muted}>아직 기록이 없습니다. 작업이 시작되면 여기에 바로 표시됩니다.</span>}
      {all.map((e) =>
        e.kind === "tool" ? (
          <details key={`${e.runId}:${e.seq}`} className={s.tool}>
            <summary>
              <span className={s.time}>{formatTime(e.at)}</span> {toolLine(e.summary)}
            </summary>
            <div className={s.args}>{e.summary}</div>
          </details>
        ) : (
          <div
            key={`${e.runId}:${e.seq}`}
            className={mergeClasses(
              s.line,
              e.kind === "warning" && s.warning,
              e.kind === "error" && s.error,
              (e.kind === "system" || e.kind === "usage") && s.muted,
            )}
          >
            <span className={s.time}>{formatTime(e.at)}</span>
            <span>{e.summary}</span>
          </div>
        ),
      )}
    </div>
  );
}
