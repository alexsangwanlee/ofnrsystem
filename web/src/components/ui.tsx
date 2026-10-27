import { useCallback, useEffect, useId, useRef, useState, type MouseEvent, type ReactNode } from "react";
import { useLinkClickHandler } from "react-router";
import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Link,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Skeleton,
  SkeletonItem,
  Subtitle2,
  Text,
  Title3,
  makeStyles,
  mergeClasses,
  tokens,
  type BadgeProps,
} from "@fluentui/react-components";
import { api } from "../api";
import { JOB_STATUS } from "../format";
import type { JobStatus } from "../types";

export const useUi = makeStyles({
  page: {
    maxWidth: "1200px",
    marginInline: "auto",
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
  },
  fill: { maxWidth: "none", height: "100%", minHeight: 0 },
  head: { display: "flex", alignItems: "flex-start", gap: tokens.spacingHorizontalM, flexWrap: "wrap" },
  titles: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS, flexGrow: 1, minWidth: 0 },
  actions: { display: "flex", gap: tokens.spacingHorizontalS, flexWrap: "wrap", alignItems: "center" },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorStatusDangerForeground1 },
  section: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS, minWidth: 0 },
  sectionHead: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, minHeight: "32px" },
  card: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    minWidth: 0,
  },
  // A framed list: one L1 surface with hairlines between rows.
  list: {
    listStyle: "none",
    margin: 0,
    padding: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    overflow: "hidden",
    "& > li + li": { borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}` },
  },
  row: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    minHeight: "40px",
    minWidth: 0,
  },
  grow: { flexGrow: 1, minWidth: 0, display: "flex", flexDirection: "column" },
  ellipsis: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  kpis: { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: tokens.spacingHorizontalM },
  kpi: {
    display: "flex",
    flexDirection: "column",
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  dl: {
    display: "grid",
    gridTemplateColumns: "max-content minmax(0, 1fr)",
    columnGap: tokens.spacingHorizontalL,
    rowGap: tokens.spacingVerticalS,
    margin: 0,
    "& dt": { color: tokens.colorNeutralForeground3 },
    "& dd": { margin: 0, overflowWrap: "anywhere" },
  },
  // Fast local responses never flash a skeleton: it fades in only after 150ms.
  skeleton: {
    display: "grid",
    gap: tokens.spacingVerticalS,
    animationName: { from: { opacity: 0 }, to: { opacity: 1 } },
    animationDuration: "1ms",
    animationDelay: "150ms",
    animationFillMode: "both",
  },
  danger: {
    backgroundColor: tokens.colorStatusDangerBackground3,
    ":hover": { backgroundColor: tokens.colorStatusDangerBackground3Hover },
    ":hover:active": { backgroundColor: tokens.colorStatusDangerBackground3Pressed },
  },
});

export interface Loaded<T> {
  data: T | undefined;
  error: string | undefined;
  reload: () => void;
  update: (f: (data: T) => T) => void;
}

/** GETs `path` (null skips). A reload keeps showing the previous data; a new path starts empty. */
export function useApi<T>(path: string | null): Loaded<T> {
  const [s, set] = useState<{ path: string | null; data?: T; error?: string }>({ path: null });
  const seq = useRef(0);
  const reload = useCallback(() => {
    const n = ++seq.current;
    if (!path) return;
    set((p) => (p.path === path ? { ...p, error: undefined } : p));
    api<T>(path).then(
      (data) => {
        if (n === seq.current) set({ path, data });
      },
      (e: Error) => {
        if (n === seq.current) set((p) => ({ path, data: p.path === path ? p.data : undefined, error: e.message }));
      },
    );
  }, [path]);
  useEffect(reload, [reload]);
  const update = useCallback(
    (f: (data: T) => T) => set((p) => (p.path === path && p.data !== undefined ? { ...p, data: f(p.data) } : p)),
    [path],
  );
  const own = s.path === path;
  return { data: own ? s.data : undefined, error: own ? s.error : undefined, reload, update };
}

/** Runs one user action at a time and keeps the server's Korean error message for inline display. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const run = useCallback(async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try {
      await fn();
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, run };
}

/** Blur-first validation: errors show after a field is left, hide while it is being corrected. */
export function useValidation(errors: Record<string, string | undefined>) {
  const base = useId();
  const [touched, setTouched] = useState<ReadonlySet<string>>(new Set());
  const [editing, setEditing] = useState<string>();
  return {
    id: (name: string) => `${base}-${name}`,
    blur: (name: string) => () => {
      setEditing(undefined);
      setTouched((t) => new Set(t).add(name));
    },
    edit: (name: string) => setEditing(name),
    message: (name: string) => (touched.has(name) && editing !== name ? errors[name] : undefined),
    /** Shows every error and focuses the first; true when the form is valid. */
    check: () => {
      setEditing(undefined);
      setTouched(new Set(Object.keys(errors)));
      const first = Object.keys(errors).find((k) => errors[k]);
      if (first) document.getElementById(`${base}-${first}`)?.focus();
      return !first;
    },
  };
}

export function Page({
  title,
  description,
  actions,
  fill,
  children,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  fill?: boolean;
  children: ReactNode;
}) {
  const s = useUi();
  return (
    <div className={mergeClasses(s.page, fill && s.fill)}>
      <header className={s.head}>
        <div className={s.titles}>
          <Title3 as="h1">{title}</Title3>
          {description && <div className={s.muted}>{description}</div>}
        </div>
        {actions && <div className={s.actions}>{actions}</div>}
      </header>
      {children}
    </div>
  );
}

export function Section({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  const s = useUi();
  const id = useId();
  return (
    <section aria-labelledby={id} className={s.section}>
      <div className={s.sectionHead}>
        <Subtitle2 as="h2" id={id} style={{ flexGrow: 1 }}>
          {title}
        </Subtitle2>
        {actions}
      </div>
      {children}
    </section>
  );
}

export function Kpi({ label, value }: { label: string; value: ReactNode }) {
  const s = useUi();
  const id = useId();
  return (
    <div role="group" aria-labelledby={id} className={s.kpi}>
      <Caption1 id={id} className={s.muted}>
        {label}
      </Caption1>
      <Text size={700} weight="semibold">
        {value}
      </Text>
    </div>
  );
}

export function ErrorBar({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <MessageBar intent="error">
      <MessageBarBody>{message}</MessageBarBody>
      {onRetry && (
        <MessageBarActions>
          <Button size="small" onClick={onRetry}>
            다시 시도
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/** Data, else the load error with 다시 시도, else a row skeleton. */
export function Async<T>({ state, rows = 3, children }: { state: Loaded<T>; rows?: number; children: (data: T) => ReactNode }) {
  const s = useUi();
  if (state.data !== undefined) return <>{children(state.data)}</>;
  if (state.error) return <ErrorBar message={state.error} onRetry={state.reload} />;
  return (
    <Skeleton aria-label="불러오는 중" className={s.skeleton}>
      {Array.from({ length: rows }, (_, i) => (
        <SkeletonItem key={i} size={40} />
      ))}
    </Skeleton>
  );
}

/** Confirmation with 취소 first in focus order, so Enter or Escape never confirms by accident. */
export function Confirm({
  open,
  title,
  body,
  confirm,
  danger,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  body: ReactNode;
  confirm: string;
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const s = useUi();
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onCancel()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>{body}</DialogContent>
          <DialogActions>
            <Button onClick={onCancel}>취소</Button>
            <Button appearance="primary" className={danger ? s.danger : undefined} disabled={busy} onClick={onConfirm}>
              {confirm}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function RouterLink({ to, children, className }: { to: string; children: ReactNode; className?: string }) {
  const onClick = useLinkClickHandler<HTMLAnchorElement>(to);
  return (
    <Link href={to} className={className} onClick={(e: MouseEvent<HTMLAnchorElement>) => onClick(e)}>
      {children}
    </Link>
  );
}

const STATUS_COLOR: Record<JobStatus, BadgeProps["color"]> = {
  Queued: "informative",
  Running: "brand",
  NeedsAttention: "warning",
  Review: "important",
  Done: "success",
  Canceled: "subtle",
  Failed: "danger",
};

export function JobStatusBadge({ status }: { status: JobStatus }) {
  return (
    <Badge appearance="tint" shape="rounded" color={STATUS_COLOR[status]}>
      {JOB_STATUS[status]}
    </Badge>
  );
}
