import { useState } from "react";
import {
  Badge,
  Button,
  Caption1,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  SearchBox,
  Subtitle2,
  Tooltip,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { Add16Regular, Add20Regular, Dismiss16Regular, MoreHorizontal16Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { EmptyState } from "../components/EmptyState";
import { NewJobDialog } from "../components/NewJobDialog";
import { ProjectForm } from "../components/ProjectForm";
import { Async, ErrorBar, Page, RouterLink, useAction, useApi, useUi } from "../components/ui";
import { DATE_KINDS, daysUntil, dday, formatKrw, stageLabel } from "../format";
import { useOps } from "../realtime";
import { STAGES, type ProjectDto, type Stage, type StageMoveResult } from "../types";

const useStyles = makeStyles({
  board: {
    flexGrow: 1,
    minHeight: 0,
    display: "grid",
    gridAutoFlow: "column",
    gridAutoColumns: "minmax(156px, 1fr)",
    gap: tokens.spacingHorizontalS,
    overflowX: "auto",
    paddingBottom: tokens.spacingVerticalS,
  },
  column: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    minHeight: 0,
    overflowY: "auto",
    padding: tokens.spacingHorizontalS,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusLarge,
  },
  // Always two lines (stage and count, then total) so every column header has the same height.
  colHead: {
    display: "flex",
    flexDirection: "column",
    paddingInline: tokens.spacingHorizontalXS,
    whiteSpace: "nowrap",
    fontVariantNumeric: "tabular-nums",
  },
  colTitle: { display: "flex", alignItems: "baseline", columnGap: tokens.spacingHorizontalS },
  sum: { textAlign: "end", color: tokens.colorNeutralForeground3 },
  card: { gap: tokens.spacingVerticalXS, cursor: "grab" },
  cardTop: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalXS },
  name: { fontWeight: tokens.fontWeightSemibold },
  badges: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalXS, alignItems: "center" },
  chips: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalS, marginTop: tokens.spacingVerticalXS },
});

const DATE_FIELDS = ["install", "open", "end", "teardown"] as const;

/** The nearest install/open/end/teardown date that is today or later. */
function nearestDate(p: ProjectDto) {
  let best: { label: string; date: string; days: number } | undefined;
  for (const k of DATE_FIELDS) {
    const date = p[`${k}Date` as const];
    const days = date ? daysUntil(date) : -1;
    if (date && days >= 0 && (!best || days < best.days)) best = { label: DATE_KINDS[k], date, days };
  }
  return best;
}

const amountOf = (p: ProjectDto) => p.contractAmount ?? p.quoteAmount ?? 0;

export function Board() {
  const s = useStyles();
  const ui = useUi();
  const projects = useApi<ProjectDto[]>("/api/projects");
  const [q, setQ] = useState("");
  const [formStage, setFormStage] = useState<Stage>();
  const [moved, setMoved] = useState<StageMoveResult>();
  const [jobFor, setJobFor] = useState<{ project: ProjectDto; recipeId: string }>();
  const { error, run } = useAction();
  // The server sends projectUpdated before the create response, so both paths must add only once.
  const upsert = (p: ProjectDto) =>
    projects.update((l) => (l.some((x) => x.id === p.id) ? l.map((x) => (x.id === p.id ? p : x)) : [...l, p]));

  useOps({ projectUpdated: upsert, connected: projects.reload });

  const move = (p: ProjectDto, stage: Stage) => {
    if (p.stage === stage) return;
    void run(async () => {
      const r = await api<StageMoveResult>(`/api/projects/${p.id}/stage`, { method: "POST", body: JSON.stringify({ stage }) });
      projects.update((l) => l.map((x) => (x.id === p.id ? r.project : x)));
      setMoved(r);
    });
  };

  const needle = q.trim().toLowerCase();
  const matches = (p: ProjectDto) =>
    !needle || [p.name, p.clientName, p.jobNo, p.venue].some((t) => t?.toLowerCase().includes(needle));

  return (
    <Page
      fill
      title="프로젝트"
      description="프로젝트를 단계별로 보고 옮깁니다. 단계를 옮기면 그 단계의 추천 작업을 제안합니다."
      actions={
        <Button appearance="primary" icon={<Add20Regular />} onClick={() => setFormStage("Inquiry")}>
          새 프로젝트
        </Button>
      }
    >
      <SearchBox
        style={{ maxWidth: "360px" }}
        placeholder="프로젝트, 거래처, 작업번호 검색"
        aria-label="프로젝트 검색"
        value={q}
        onChange={(_, d) => setQ(d.value)}
      />
      {error && <ErrorBar message={error} />}
      {moved && (
        <MessageBar intent={moved.warnings.length ? "warning" : "success"} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {moved.project.name}: {stageLabel(moved.project.stage)} 단계로 옮겼습니다.
            </MessageBarTitle>
            {moved.warnings.join(" ")}
            {moved.suggestions.length > 0 && (
              <div className={s.chips}>
                {moved.suggestions.map((r) => (
                  <Button
                    key={r.id}
                    size="small"
                    shape="circular"
                    onClick={() => setJobFor({ project: moved.project, recipeId: r.id })}
                  >
                    작업 맡기기: {r.name}
                  </Button>
                ))}
              </div>
            )}
          </MessageBarBody>
          <MessageBarActions
            containerAction={
              <Button appearance="transparent" aria-label="닫기" icon={<Dismiss16Regular />} onClick={() => setMoved(undefined)} />
            }
          />
        </MessageBar>
      )}
      <Async state={projects} rows={4}>
        {(list) => (
          <>
            {list.length === 0 && (
              <div className={ui.card}>
                <EmptyState
                  title="아직 프로젝트가 없습니다"
                  body="문의가 들어오면 프로젝트를 만들어 단계별로 관리하세요. 메일에서 바로 만들 수도 있습니다."
                  action={
                    <Button appearance="primary" onClick={() => setFormStage("Inquiry")}>
                      새 프로젝트 만들기
                    </Button>
                  }
                />
              </div>
            )}
            <div className={s.board}>
              {STAGES.map((stage) => {
                const items = list.filter((p) => p.stage === stage && matches(p));
                const sum = items.reduce((t, p) => t + amountOf(p), 0);
                return (
                  <section
                    key={stage}
                    aria-labelledby={`col-${stage}`}
                    className={s.column}
                    onDragOver={(e) => e.preventDefault()}
                    onDrop={(e) => {
                      const p = list.find((x) => x.id === e.dataTransfer.getData("text/plain"));
                      if (p) move(p, stage);
                    }}
                  >
                    <div className={s.colHead}>
                      <div className={s.colTitle}>
                        <Subtitle2 as="h2" id={`col-${stage}`}>
                          {stageLabel(stage)}
                        </Subtitle2>
                        <Caption1 className={ui.muted}>{items.length}건</Caption1>
                      </div>
                      <Caption1 className={s.sum}>{formatKrw(sum)}</Caption1>
                    </div>
                    {items.map((p) => (
                      <ProjectCard key={p.id} p={p} onMove={move} />
                    ))}
                    <Button appearance="subtle" size="small" icon={<Add16Regular />} onClick={() => setFormStage(stage)}>
                      추가
                    </Button>
                  </section>
                );
              })}
            </div>
          </>
        )}
      </Async>
      <ProjectForm
        open={!!formStage}
        title="새 프로젝트"
        seed={{ name: "", stage: formStage }}
        onClose={() => setFormStage(undefined)}
        onSubmit={async (input) => {
          upsert(await api<ProjectDto>("/api/projects", { method: "POST", body: JSON.stringify(input) }));
        }}
      />
      <NewJobDialog
        open={!!jobFor}
        onClose={() => setJobFor(undefined)}
        projectId={jobFor?.project.id}
        stage={jobFor?.project.stage}
        recipeId={jobFor?.recipeId}
      />
    </Page>
  );
}

function ProjectCard({ p, onMove }: { p: ProjectDto; onMove: (p: ProjectDto, stage: Stage) => void }) {
  const s = useStyles();
  const ui = useUi();
  const next = nearestDate(p);
  const amount = p.contractAmount ?? p.quoteAmount;
  return (
    <article
      className={`${ui.card} ${s.card}`}
      draggable
      onDragStart={(e) => e.dataTransfer.setData("text/plain", p.id)}
      aria-label={p.name}
    >
      <div className={s.cardTop}>
        <Caption1 className={ui.muted} style={{ flexGrow: 1 }}>
          {p.clientName ?? "거래처 없음"}
        </Caption1>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content="단계 이동" relationship="label">
              <Button size="small" appearance="subtle" icon={<MoreHorizontal16Regular />} />
            </Tooltip>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              {STAGES.map((st) => (
                <MenuItem key={st} disabled={st === p.stage} onClick={() => onMove(p, st)}>
                  {stageLabel(st)}
                </MenuItem>
              ))}
            </MenuList>
          </MenuPopover>
        </Menu>
      </div>
      <RouterLink to={`/projects/${p.id}`} className={s.name}>
        {p.name}
      </RouterLink>
      <Caption1 className={ui.muted}>{p.jobNo}</Caption1>
      <div className={s.badges}>
        {next && (
          <Badge appearance="outline" shape="rounded">
            {next.label} {dday(next.date)}
          </Badge>
        )}
        {amount != null && (
          <Caption1>
            {p.contractAmount != null ? "계약" : "견적"} {formatKrw(amount)}
          </Caption1>
        )}
      </div>
      {(p.attentionCount > 0 || p.runningCount > 0 || p.status === "OnHold" || p.status === "Lost") && (
        <div className={s.badges}>
          {p.attentionCount > 0 && (
            <Badge appearance="tint" shape="rounded" color="warning">
              확인 필요 {p.attentionCount}
            </Badge>
          )}
          {p.runningCount > 0 && (
            <Badge appearance="tint" shape="rounded" color="brand">
              실행 중 {p.runningCount}
            </Badge>
          )}
          {p.status === "OnHold" && (
            <Badge appearance="tint" shape="rounded" color="subtle">
              보류
            </Badge>
          )}
          {p.status === "Lost" && (
            <Badge appearance="tint" shape="rounded" color="subtle">
              실주
            </Badge>
          )}
        </div>
      )}
    </article>
  );
}
