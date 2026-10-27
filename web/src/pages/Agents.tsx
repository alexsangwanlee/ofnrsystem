import { useNavigate } from "react-router";
import { Badge, Body1, Button, Caption1, makeStyles, tokens } from "@fluentui/react-components";
import { api } from "../api";
import { EmptyState } from "../components/EmptyState";
import { Async, ErrorBar, JobStatusBadge, Kpi, Page, RouterLink, Section, useAction, useApi, useUi } from "../components/ui";
import { ENGINE, formatUsd, lockLabel } from "../format";
import { useOps } from "../realtime";
import type { AgentsDto, EngineDto, JobDto, RecipeDto } from "../types";

const useStyles = makeStyles({
  grid: { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(380px, 1fr))", gap: tokens.spacingHorizontalXL, alignItems: "start" },
  column: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
  num: { fontVariantNumeric: "tabular-nums", flexShrink: 0 },
});

function JobRows({ jobs, empty }: { jobs: JobDto[]; empty: string }) {
  const ui = useUi();
  const navigate = useNavigate();
  if (jobs.length === 0)
    return (
      <div className={ui.card}>
        <EmptyState
          title={empty}
          body="프로젝트나 메일에서 AI에게 작업을 맡기면 여기에 표시됩니다."
          action={<Button onClick={() => navigate("/projects")}>프로젝트 보기</Button>}
        />
      </div>
    );
  return (
    <ul className={ui.list}>
      {jobs.map((j) => (
        <li key={j.id} className={ui.row}>
          <div className={ui.grow}>
            <RouterLink to={`/jobs/${j.id}`} className={ui.ellipsis}>
              {j.title}
            </RouterLink>
            <Caption1 className={ui.muted}>
              {j.recipeName} · {ENGINE[j.engine]}
            </Caption1>
          </div>
          <JobStatusBadge status={j.status} />
        </li>
      ))}
    </ul>
  );
}

export function Agents() {
  const s = useStyles();
  const ui = useUi();
  const agents = useApi<AgentsDto>("/api/agents");
  const recipes = useApi<RecipeDto[]>("/api/recipes");
  const check = useAction();
  useOps({ jobUpdated: agents.reload, connected: agents.reload });

  const recipeName = (id: string) => recipes.data?.find((r) => r.id === id)?.name ?? id;
  const runCheck = (name: string) =>
    void check.run(async () => {
      const e = await api<EngineDto>(`/api/health/engine-check/${name}`, { method: "POST" });
      agents.update((a) => ({ ...a, engines: a.engines.map((x) => (x.name === e.name ? e : x)) }));
    });

  return (
    <Page title="에이전트" description="AI 작업의 실행 상태, 앱 잠금, 이번 달 추정 비용과 엔진 상태입니다.">
      <Async state={agents} rows={6}>
        {(a) => (
          <>
            <div className={ui.kpis}>
              <Kpi label="실행 중" value={a.running.length} />
              <Kpi label="대기" value={a.queued.length} />
              <Kpi label="이번 달 추정 비용" value={formatUsd(a.costs.monthUsd)} />
            </div>
            <div className={s.grid}>
              <div className={s.column}>
                <Section title="실행 중">
                  <JobRows jobs={a.running} empty="실행 중인 작업이 없습니다" />
                </Section>
                <Section title="대기">
                  <JobRows jobs={a.queued} empty="대기 중인 작업이 없습니다" />
                </Section>
              </div>
              <div className={s.column}>
                <Section title="앱 잠금">
                  <ul className={ui.list}>
                    {a.locks.map((l) => (
                      <li key={l.name} className={ui.row}>
                        <Body1 className={ui.grow}>
                          <span>
                            {lockLabel(l.name)}:{" "}
                            {l.holderJobId ? (
                              <RouterLink to={`/jobs/${l.holderJobId}`}>{`작업 #${l.holderJobId.slice(0, 8)}`}</RouterLink>
                            ) : (
                              "사용 가능"
                            )}
                            {l.holderJobId && " 사용 중"}
                            {l.waiting > 0 && `, 대기 ${l.waiting}`}
                          </span>
                        </Body1>
                      </li>
                    ))}
                  </ul>
                </Section>
                <Section title="엔진">
                  {check.error && <ErrorBar message={check.error} />}
                  <ul className={ui.list}>
                    {a.engines.map((e) => (
                      <li key={e.name} className={ui.row}>
                        <div className={ui.grow}>
                          <Body1>
                            {e.name} {e.version ?? "버전 확인 불가"}
                          </Body1>
                          <Caption1 className={ui.muted}>{e.detail ?? e.path}</Caption1>
                        </div>
                        {!e.ok && (
                          <Badge appearance="tint" shape="rounded" color="danger">
                            오류
                          </Badge>
                        )}
                        {e.ok && !e.loggedIn && (
                          <Badge appearance="tint" shape="rounded" color="warning">
                            로그인 필요
                          </Badge>
                        )}
                        {e.version && e.version !== e.pinned && (
                          <Badge appearance="tint" shape="rounded" color="warning">
                            고정 버전 {e.pinned}과 다름
                          </Badge>
                        )}
                        <Button size="small" disabled={check.busy} onClick={() => runCheck(e.name)}>
                          점검
                        </Button>
                      </li>
                    ))}
                  </ul>
                </Section>
                <Section title="이번 달 레시피별 비용">
                  {a.costs.byRecipe.length === 0 ? (
                    <Body1 className={ui.muted}>이번 달에 실행한 작업이 없습니다.</Body1>
                  ) : (
                    <ul className={ui.list}>
                      {a.costs.byRecipe.map((r) => (
                        <li key={r.recipeId} className={ui.row}>
                          <Body1 className={ui.grow}>{recipeName(r.recipeId)}</Body1>
                          <Caption1 className={`${ui.muted} ${s.num}`}>{r.runs}회</Caption1>
                          <Body1 className={s.num}>{formatUsd(r.usd)}</Body1>
                        </li>
                      ))}
                    </ul>
                  )}
                </Section>
              </div>
            </div>
          </>
        )}
      </Async>
    </Page>
  );
}
