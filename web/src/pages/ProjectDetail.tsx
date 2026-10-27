import { useState } from "react";
import { useNavigate, useParams } from "react-router";
import {
  Body1,
  Button,
  Caption1,
  Field,
  Tab,
  TabList,
  Text,
  Textarea,
  makeStyles,
  mergeClasses,
  tokens,
} from "@fluentui/react-components";
import { FolderOpen20Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { EmptyState } from "../components/EmptyState";
import { FileList, openPath } from "../components/FileList";
import { NewJobDialog } from "../components/NewJobDialog";
import { ProjectForm, toInput } from "../components/ProjectForm";
import { StageBadge } from "../components/StageBadge";
import { Async, ErrorBar, JobStatusBadge, Page, RouterLink, useAction, useApi, useUi } from "../components/ui";
import { MAIL_STATUS, formatDate, formatKrw, formatTime, formatUsd, stageLabel } from "../format";
import { useOps } from "../realtime";
import { STAGES, type ProjectDetailDto, type ProjectDto, type RecipeDto } from "../types";

const TABS = [
  ["overview", "개요"],
  ["mail", "메일"],
  ["jobs", "작업"],
  ["files", "파일"],
  ["activity", "활동"],
  ["instructions", "지침"],
] as const;
type TabId = (typeof TABS)[number][0];

const useStyles = makeStyles({
  meta: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, flexWrap: "wrap" },
  stepper: { display: "flex", listStyle: "none", margin: 0, padding: 0, gap: "2px", overflowX: "auto" },
  step: {
    flex: "1 0 72px",
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    textAlign: "center",
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    backgroundColor: tokens.colorNeutralBackground3,
    ":first-child": { borderRadius: `${tokens.borderRadiusMedium} 0 0 ${tokens.borderRadiusMedium}` },
    ":last-child": { borderRadius: `0 ${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium} 0` },
  },
  past: { color: tokens.colorNeutralForeground2, backgroundColor: tokens.colorNeutralBackground5 },
  current: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: tokens.colorBrandBackground,
    fontWeight: tokens.fontWeightSemibold,
  },
  chips: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalS, alignItems: "center" },
  body: { display: "grid", gridTemplateColumns: "140px minmax(0, 1fr)", gap: tokens.spacingHorizontalXL, alignItems: "start" },
  pre: { whiteSpace: "pre-wrap", overflowWrap: "anywhere" },
});

export function ProjectDetail() {
  const s = useStyles();
  const ui = useUi();
  const { id } = useParams();
  const navigate = useNavigate();
  const detail = useApi<ProjectDetailDto>(`/api/projects/${id}`);
  const recipes = useApi<RecipeDto[]>("/api/recipes");
  const [tab, setTab] = useState<TabId>("overview");
  const [editing, setEditing] = useState(false);
  const [job, setJob] = useState<{ recipeId?: string }>();
  const action = useAction();

  useOps({
    projectUpdated: (p) => p.id === id && detail.reload(),
    jobUpdated: (j) => j.projectId === id && detail.reload(),
    mailUpdated: (m) => m.projectId === id && detail.reload(),
    connected: detail.reload,
  });

  const put = async (p: ProjectDto, input: object) => {
    await api(`/api/projects/${p.id}`, { method: "PUT", body: JSON.stringify({ ...toInput(p), ...input }) });
    detail.reload();
  };

  return (
    <Async state={detail} rows={8}>
      {({ project: p, mails, jobs, activity }) => {
        const current = STAGES.indexOf(p.stage);
        const suggested = (recipes.data ?? []).filter((r) => r.stage === p.stage);
        return (
          <Page
            title={p.name}
            description={
              <div className={s.meta}>
                <RouterLink to="/projects">프로젝트</RouterLink>
                <span>›</span>
                <span>{p.jobNo}</span>
                <StageBadge stage={p.stage} />
                {p.clientName && <span>{p.clientName}</span>}
                {p.venue && <span>· {p.venue}</span>}
              </div>
            }
            actions={
              <>
                <Button icon={<FolderOpen20Regular />} onClick={() => void action.run(() => openPath(p.folderPath))}>
                  폴더 열기
                </Button>
                <Button onClick={() => setEditing(true)}>수정</Button>
                <Button appearance="primary" onClick={() => setJob({})}>
                  작업 맡기기
                </Button>
              </>
            }
          >
            {action.error && <ErrorBar message={action.error} />}
            <ol className={s.stepper} aria-label="진행 단계">
              {STAGES.map((st, i) => (
                <li
                  key={st}
                  aria-current={i === current ? "step" : undefined}
                  className={mergeClasses(s.step, i < current && s.past, i === current && s.current)}
                >
                  {stageLabel(st)}
                </li>
              ))}
            </ol>
            {suggested.length > 0 && (
              <div className={s.chips}>
                <Caption1 className={ui.muted}>{stageLabel(p.stage)} 단계 추천 작업</Caption1>
                {suggested.map((r) => (
                  <Button key={r.id} size="small" shape="circular" onClick={() => setJob({ recipeId: r.id })}>
                    작업 맡기기: {r.name}
                  </Button>
                ))}
              </div>
            )}
            <div className={s.body}>
              <TabList vertical selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as TabId)}>
                {TABS.map(([value, label]) => (
                  <Tab key={value} value={value}>
                    {label}
                  </Tab>
                ))}
              </TabList>
              <div role="tabpanel" aria-label={TABS.find((t) => t[0] === tab)![1]}>
                {tab === "overview" && (
                  <dl className={ui.dl}>
                    <dt>거래처</dt>
                    <dd>{p.clientName ?? "없음"}</dd>
                    <dt>장소</dt>
                    <dd>{p.venue ?? "없음"}</dd>
                    {(
                      [
                        ["설치일", p.installDate],
                        ["오픈일", p.openDate],
                        ["종료일", p.endDate],
                        ["철수일", p.teardownDate],
                      ] as const
                    ).map(([label, d]) => (
                      <div key={label} style={{ display: "contents" }}>
                        <dt>{label}</dt>
                        <dd>{d ? formatDate(d) : "미정"}</dd>
                      </div>
                    ))}
                    {(
                      [
                        ["견적액", p.quoteAmount],
                        ["계약액", p.contractAmount],
                        ["실행원가", p.costAmount],
                      ] as const
                    ).map(([label, n]) => (
                      <div key={label} style={{ display: "contents" }}>
                        <dt>{label}</dt>
                        <dd>{n != null ? formatKrw(n) : "미정"}</dd>
                      </div>
                    ))}
                    <dt>폴더</dt>
                    <dd>{p.folderPath}</dd>
                  </dl>
                )}
                {tab === "mail" &&
                  (mails.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="연결된 메일이 없습니다"
                        body="메일함에서 이 프로젝트에 메일을 연결하면 여기에 모입니다."
                        action={<Button onClick={() => navigate("/mail")}>메일함 열기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {mails.map((m) => (
                        <li key={m.id} className={ui.row}>
                          <div className={ui.grow}>
                            <RouterLink to={`/mail/${m.id}`} className={ui.ellipsis}>
                              {m.subject}
                            </RouterLink>
                            <Caption1 className={ui.muted}>
                              {m.fromName ?? m.fromAddress} · {formatDate(m.receivedAt)} · {MAIL_STATUS[m.status]}
                            </Caption1>
                          </div>
                        </li>
                      ))}
                    </ul>
                  ))}
                {tab === "jobs" &&
                  (jobs.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="아직 맡긴 작업이 없습니다"
                        body="견적서, 제안서, 3D 모델 같은 작업을 AI에게 맡기면 여기에 쌓입니다."
                        action={<Button onClick={() => setJob({})}>작업 맡기기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {jobs.map((j) => (
                        <li key={j.id} className={ui.row}>
                          <div className={ui.grow}>
                            <RouterLink to={`/jobs/${j.id}`} className={ui.ellipsis}>
                              {j.title}
                            </RouterLink>
                            <Caption1 className={ui.muted}>
                              {j.recipeName} · {formatDate(j.updatedAt)}
                              {j.costUsd != null && ` · 추정 ${formatUsd(j.costUsd)}`}
                            </Caption1>
                          </div>
                          <JobStatusBadge status={j.status} />
                        </li>
                      ))}
                    </ul>
                  ))}
                {tab === "files" && <FileList projectId={p.id} folderPath={p.folderPath} />}
                {tab === "activity" &&
                  (activity.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="활동 기록이 없습니다"
                        body="단계 이동, 메일 연결, 작업 결과가 시간순으로 기록됩니다."
                        action={<Button onClick={() => setJob({})}>작업 맡기기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {[...activity]
                        .sort((a, b) => b.at.localeCompare(a.at))
                        .map((a) => (
                          <li key={a.id} className={ui.row}>
                            <Caption1 className={ui.muted} style={{ flexShrink: 0 }}>
                              {formatDate(a.at)} {formatTime(a.at)}
                            </Caption1>
                            <Body1 className={ui.grow}>{a.summary}</Body1>
                          </li>
                        ))}
                    </ul>
                  ))}
                {tab === "instructions" && (
                  <Instructions key={p.id} value={p.instructions ?? ""} onSave={(instructions) => put(p, { instructions })} />
                )}
              </div>
            </div>
            <ProjectForm
              open={editing}
              editing
              title="프로젝트 수정"
              seed={toInput(p)}
              onClose={() => setEditing(false)}
              onSubmit={(input) => put(p, input)}
            />
            <NewJobDialog
              open={!!job}
              onClose={() => setJob(undefined)}
              projectId={p.id}
              stage={p.stage}
              recipeId={job?.recipeId}
            />
          </Page>
        );
      }}
    </Async>
  );
}

function Instructions({ value, onSave }: { value: string; onSave: (v: string | null) => Promise<void> }) {
  const ui = useUi();
  const s = useStyles();
  const [draft, setDraft] = useState<string>();
  const { busy, error, run } = useAction();
  if (draft === undefined)
    return (
      <div className={ui.section}>
        <Body1 className={s.pre}>
          {value || "아직 지침이 없습니다. 거래처의 요청 사항이나 AI가 늘 지켜야 할 내용을 적어 두세요."}
        </Body1>
        <div>
          <Button onClick={() => setDraft(value)}>지침 수정</Button>
        </div>
      </div>
    );
  return (
    <div className={ui.section}>
      <Field label="지침" hint="AI가 이 프로젝트의 모든 작업에서 지킬 내용입니다.">
        <Textarea rows={10} resize="vertical" value={draft} onChange={(_, d) => setDraft(d.value)} />
      </Field>
      {error && <Text className={ui.error}>{error}</Text>}
      <div className={ui.actions}>
        <Button
          appearance="primary"
          disabled={busy}
          onClick={() => void run(() => onSave(draft.trim() || null)).then((ok) => ok && setDraft(undefined))}
        >
          저장
        </Button>
        <Button onClick={() => setDraft(undefined)}>취소</Button>
      </div>
    </div>
  );
}
