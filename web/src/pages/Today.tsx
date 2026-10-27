import { useEffect, useState } from "react";
import { useNavigate } from "react-router";
import { Badge, Body1, Button, Caption1, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { ApprovalCard } from "../components/ApprovalCard";
import { EmptyState } from "../components/EmptyState";
import { Async, Kpi, Page, RouterLink, Section, useApi, useFlash, useUi } from "../components/ui";
import { DATE_KINDS, dday, formatDate } from "../format";
import { joinJob, leaveJob, useOps } from "../realtime";
import type { TodayDto } from "../types";
import { LinkProjectMenu } from "./Mail";

const useStyles = makeStyles({
  grid: { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(380px, 1fr))", gap: tokens.spacingHorizontalXL, alignItems: "start" },
  cards: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS },
  dday: { flexShrink: 0, minWidth: "56px", justifyContent: "center" },
});

export function Today() {
  const s = useStyles();
  const ui = useUi();
  const navigate = useNavigate();
  const today = useApi<TodayDto>("/api/today");
  const [lastLine, setLastLine] = useState<Record<string, string>>({});
  const [answered, setAnswered] = useFlash();

  useOps({
    jobUpdated: today.reload,
    approvalRequested: today.reload,
    approvalResolved: today.reload,
    mailUpdated: today.reload,
    projectUpdated: today.reload,
    connected: today.reload,
    jobEvent: (e) => {
      if (e.kind === "text" || e.kind === "tool") setLastLine((l) => ({ ...l, [e.jobId]: e.summary }));
    },
  });

  // jobEvent only reaches clients that joined the job's group, so join every running job shown here.
  const running = today.data?.running.map((j) => j.id).join(",") ?? "";
  useEffect(() => {
    const ids = running ? running.split(",") : [];
    ids.forEach((id) => joinJob(id).catch(() => undefined));
    return () => ids.forEach((id) => void leaveJob(id).catch(() => undefined));
  }, [running]);

  return (
    <Page title="오늘" description="확인할 일, 새 메일, 다가오는 일정과 실행 중인 작업입니다.">
      <Async state={today} rows={6}>
        {(t) => (
          <>
            <div className={ui.kpis}>
              <Kpi label="확인 필요" value={t.counts.attention} />
              <Kpi label="실행 중" value={t.counts.running} />
              <Kpi label="7일 안 일정" value={t.counts.thisWeek} />
            </div>
            <div className={s.grid}>
              <Section title="확인 필요">
                <Caption1 aria-live="polite" className={ui.live}>
                  {answered}
                </Caption1>
                {t.attention.length === 0 ? (
                  <div className={ui.card}>
                    <EmptyState
                      title="확인할 일이 없습니다"
                      body="AI의 질문, 검토할 결과물, 실패한 작업이 생기면 여기에 모입니다."
                      action={<Button onClick={() => navigate("/projects")}>프로젝트 보기</Button>}
                    />
                  </div>
                ) : (
                  <div className={s.cards}>
                    {t.attention.map((a) => (
                      <ApprovalCard
                        key={a.approvalId ?? `${a.kind}-${a.jobId}`}
                        item={a}
                        onDone={(message) => {
                          setAnswered(message);
                          today.reload();
                        }}
                      />
                    ))}
                  </div>
                )}
              </Section>
              <div className={s.cards}>
                <Section title="미분류 메일">
                  {t.unsortedMail.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="미분류 메일이 없습니다"
                        body="프로젝트에 연결되지 않은 새 메일이 여기에 표시됩니다."
                        action={<Button onClick={() => navigate("/mail")}>메일함 열기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {t.unsortedMail.map((m) => (
                        <li key={m.id} className={ui.row}>
                          <div className={ui.grow}>
                            <RouterLink to={`/mail/${m.id}`} className={ui.ellipsis}>
                              {m.subject}
                            </RouterLink>
                            <Caption1 className={mergeClasses(ui.muted, ui.ellipsis)}>
                              {m.fromName ?? m.fromAddress} · {formatDate(m.receivedAt)}
                            </Caption1>
                          </div>
                          <LinkProjectMenu mail={m} onLinked={today.reload} />
                        </li>
                      ))}
                    </ul>
                  )}
                </Section>
                <Section title="다가오는 일정">
                  {t.upcoming.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="2주 안에 잡힌 일정이 없습니다"
                        body="프로젝트에 설치, 오픈, 종료, 철수 일자를 넣으면 여기에 D-day로 표시됩니다."
                        action={<Button onClick={() => navigate("/projects")}>프로젝트 보기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {t.upcoming.map((u) => (
                        <li key={`${u.projectId}-${u.kind}`} className={ui.row}>
                          <Badge appearance="tint" shape="rounded" className={s.dday}>
                            {dday(u.date)}
                          </Badge>
                          <div className={ui.grow}>
                            <RouterLink to={`/projects/${u.projectId}`} className={ui.ellipsis}>
                              {u.projectName}
                            </RouterLink>
                            <Caption1 className={ui.muted}>
                              {u.jobNo} · {DATE_KINDS[u.kind]} {formatDate(u.date)}
                            </Caption1>
                          </div>
                        </li>
                      ))}
                    </ul>
                  )}
                </Section>
                <Section title="실행 중 작업">
                  {t.running.length === 0 ? (
                    <div className={ui.card}>
                      <EmptyState
                        title="실행 중인 작업이 없습니다"
                        body="프로젝트나 메일에서 AI에게 작업을 맡기면 진행 상황이 여기에 보입니다."
                        action={<Button onClick={() => navigate("/projects")}>프로젝트 보기</Button>}
                      />
                    </div>
                  ) : (
                    <ul className={ui.list}>
                      {t.running.map((j) => (
                        <li key={j.id} className={ui.row}>
                          <div className={ui.grow}>
                            <RouterLink to={`/jobs/${j.id}`} className={ui.ellipsis}>
                              {j.title}
                            </RouterLink>
                            <Body1 className={mergeClasses(ui.muted, ui.ellipsis)}>{lastLine[j.id] ?? j.summary ?? "시작을 기다리는 중입니다."}</Body1>
                          </div>
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
