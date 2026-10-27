import { useState } from "react";
import { useParams } from "react-router";
import { Body1, Button, Caption1, Field, Select, Text, Textarea, makeStyles, tokens } from "@fluentui/react-components";
import { Checkmark20Regular, FolderOpen20Regular, Stop20Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { ApprovalCard } from "../components/ApprovalCard";
import { joinPath, openPath } from "../components/FileList";
import { JobLog } from "../components/JobLog";
import { Async, Confirm, ErrorBar, JobStatusBadge, Page, RouterLink, Section, useAction, useApi, useUi } from "../components/ui";
import { ENGINE, formatDate, formatTime, formatUsd } from "../format";
import { useOps } from "../realtime";
import type { JobDetailDto, ProjectDetailDto, SkillDto } from "../types";

const useStyles = makeStyles({
  meta: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, flexWrap: "wrap" },
  cards: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS },
  result: { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))", gap: tokens.spacingHorizontalL },
  items: { margin: 0, paddingInlineStart: tokens.spacingHorizontalXL, display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS },
  pre: { whiteSpace: "pre-wrap", overflowWrap: "anywhere" },
  feedback: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) 220px", gap: tokens.spacingHorizontalM, alignItems: "end" },
});

export function JobDetail() {
  const s = useStyles();
  const ui = useUi();
  const { id } = useParams();
  const detail = useApi<JobDetailDto>(`/api/jobs/${id}`);
  const projectId = detail.data?.job.projectId;
  const project = useApi<ProjectDetailDto>(projectId ? `/api/projects/${projectId}` : null);
  const skills = useApi<SkillDto[]>("/api/skills");
  const action = useAction();
  const [stopping, setStopping] = useState(false);
  const [followup, setFollowup] = useState<string>();
  const [followupMissing, setFollowupMissing] = useState(false);
  const [feedback, setFeedback] = useState("");
  const [skill, setSkill] = useState("");
  const [feedbackMissing, setFeedbackMissing] = useState(false);
  const [note, setNote] = useState<string>();
  const [answered, setAnswered] = useState<string>();

  useOps({
    jobUpdated: (j) => j.id === id && detail.reload(),
    approvalRequested: (a) => a.jobId === id && detail.reload(),
    approvalResolved: (a) => a.jobId === id && detail.reload(),
    connected: detail.reload,
  });

  const post = (path: string, body?: object) =>
    action.run(async () => {
      await api(`/api/jobs/${id}/${path}`, { method: "POST", body: body && JSON.stringify(body) });
      detail.reload();
    });

  const sendFollowup = async () => {
    if (!followup?.trim()) return setFollowupMissing(true);
    if (await post("followup", { instruction: followup.trim() })) setFollowup(undefined);
  };
  const sendFeedback = async () => {
    if (!feedback.trim()) return setFeedbackMissing(true);
    if (await post("feedback", { text: feedback.trim(), skillName: skill || null })) {
      setFeedback("");
      setNote(skill ? `검토 의견을 보냈습니다. ${skill} 스킬 학습 작업을 시작합니다.` : "검토 의견을 보냈습니다.");
    }
  };

  return (
    <Async state={detail} rows={8}>
      {({ job, events, approvals, result, feedback: given }) => {
        const folder = project.data?.project.folderPath;
        const pending = approvals.filter((a) => a.status === "Pending");
        const live = job.status === "Queued" || job.status === "Running";
        return (
          <Page
            title={job.title}
            description={
              <div className={s.meta}>
                {job.projectId && job.jobNo && <RouterLink to={`/projects/${job.projectId}`}>{job.jobNo}</RouterLink>}
                <span>{job.recipeName}</span>
                <span>· {ENGINE[job.engine]}</span>
                <JobStatusBadge status={job.status} />
                {job.costUsd != null && <span>추정 {formatUsd(job.costUsd)}</span>}
                <span>
                  · {formatDate(job.createdAt)} {formatTime(job.createdAt)}
                </span>
              </div>
            }
            actions={
              <>
                {folder && (
                  <Button icon={<FolderOpen20Regular />} onClick={() => void action.run(() => openPath(folder))}>
                    폴더 열기
                  </Button>
                )}
                {!live && (
                  <Button onClick={() => setFollowup(followup ?? "")}>후속 지시</Button>
                )}
                {(live || job.status === "NeedsAttention") && (
                  <Button icon={<Stop20Regular />} onClick={() => setStopping(true)}>
                    중지
                  </Button>
                )}
                {job.status === "Review" && (
                  <Button appearance="primary" icon={<Checkmark20Regular />} disabled={action.busy} onClick={() => void post("complete")}>
                    완료
                  </Button>
                )}
              </>
            }
          >
            {action.error && <ErrorBar message={action.error} />}
            {project.error && <ErrorBar message={project.error} onRetry={project.reload} />}
            {answered && <Caption1 aria-live="polite">{answered}</Caption1>}
            {pending.length > 0 && (
              <div className={s.cards}>
                {pending.map((a) => (
                  <ApprovalCard
                    key={a.id}
                    item={{ kind: "approval", jobId: a.jobId, title: a.title, detail: a.detail, approvalId: a.id, approvalKind: a.kind }}
                    onDone={(message) => {
                      setAnswered(message);
                      detail.reload();
                    }}
                  />
                ))}
              </div>
            )}
            {followup !== undefined && (
              <div className={ui.card}>
                <Field label="후속 지시" validationMessage={followupMissing ? "지시 내용을 입력해 주세요." : undefined}>
                  <Textarea
                    rows={4}
                    resize="vertical"
                    value={followup}
                    onChange={(_, d) => {
                      setFollowup(d.value);
                      setFollowupMissing(false);
                    }}
                  />
                </Field>
                <div className={ui.actions}>
                  <Button appearance="primary" disabled={action.busy} onClick={() => void sendFollowup()}>
                    보내기
                  </Button>
                  <Button onClick={() => setFollowup(undefined)}>취소</Button>
                </div>
              </div>
            )}
            {result && (
              <Section title="결과">
                <div className={ui.card}>
                  <Body1 className={s.pre}>{result.summary}</Body1>
                  <div className={s.result}>
                    <ResultList title="만든 파일" items={result.files} folder={folder} />
                    <ResultList title="확인 필요 항목" items={result.needsConfirmation} />
                    <ResultList title="다음 할 일" items={result.nextSteps} />
                  </div>
                </div>
              </Section>
            )}
            <Section title="실행 로그">
              <JobLog key={job.id} jobId={job.id} events={events} />
            </Section>
            <Section title="검토 의견">
              <div className={ui.card}>
                <div className={s.feedback}>
                  <Field
                    label="검토 의견"
                    validationMessage={feedbackMissing ? "검토 의견을 입력해 주세요." : undefined}
                  >
                    <Textarea
                      rows={3}
                      resize="vertical"
                      value={feedback}
                      onChange={(_, d) => {
                        setFeedback(d.value);
                        setFeedbackMissing(false);
                      }}
                    />
                  </Field>
                  <Field label="스킬에 반영">
                    <Select value={skill} onChange={(_, d) => setSkill(d.value)}>
                      <option value="">반영 안 함</option>
                      {(skills.data ?? []).map((k) => (
                        <option key={k.name} value={k.name}>
                          {k.name}
                        </option>
                      ))}
                    </Select>
                  </Field>
                </div>
                {skills.error && <ErrorBar message={skills.error} onRetry={skills.reload} />}
                {note && <Caption1 aria-live="polite">{note}</Caption1>}
                <div className={ui.actions}>
                  <Button appearance="primary" disabled={action.busy} onClick={() => void sendFeedback()}>
                    보내기
                  </Button>
                </div>
                {given.length > 0 && (
                  <ul className={ui.list}>
                    {given.map((f) => (
                      <li key={f.id} className={ui.row}>
                        <Body1 className={`${ui.grow} ${s.pre}`}>{f.text}</Body1>
                        {f.skillName && <Caption1 className={ui.muted}>{f.skillName}</Caption1>}
                        {f.learningJobId && <RouterLink to={`/jobs/${f.learningJobId}`}>학습 작업</RouterLink>}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </Section>
            <Confirm
              open={stopping}
              danger
              title="작업을 중지할까요?"
              body={`"${job.title}" 작업을 중지합니다. 나중에 후속 지시로 이어서 진행할 수 있습니다.`}
              confirm="작업 중지"
              onCancel={() => setStopping(false)}
              onConfirm={() => {
                setStopping(false);
                void post("cancel");
              }}
            />
          </Page>
        );
      }}
    </Async>
  );
}

function ResultList({ title, items, folder }: { title: string; items: string[]; folder?: string }) {
  const s = useStyles();
  const ui = useUi();
  const { error, run } = useAction();
  return (
    <div className={ui.section}>
      <Text weight="semibold">{title}</Text>
      {items.length === 0 ? (
        <Caption1 className={ui.muted}>없음</Caption1>
      ) : (
        <ul className={s.items}>
          {items.map((item) => (
            <li key={item}>
              {folder ? (
                <Button
                  appearance="transparent"
                  size="small"
                  onClick={() => void run(() => openPath(/^([a-z]:|[\\/])/i.test(item) ? item : joinPath(folder, item)))}
                >
                  {item}
                </Button>
              ) : (
                item
              )}
            </li>
          ))}
        </ul>
      )}
      {error && <Caption1 className={ui.error}>{error}</Caption1>}
    </div>
  );
}
