import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useNavigate, useParams } from "react-router";
import {
  Badge,
  Body1,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  SearchBox,
  Subtitle1,
  Tab,
  TabList,
  Text,
  Textarea,
  makeStyles,
  mergeClasses,
  tokens,
} from "@fluentui/react-components";
import { ArrowSync20Regular, Attach16Regular, Link20Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { EmptyState } from "../components/EmptyState";
import { joinPath } from "../components/FileList";
import { NewJobDialog } from "../components/NewJobDialog";
import { ProjectForm, type ProjectSeed } from "../components/ProjectForm";
import { Async, Confirm, ErrorBar, Page, RouterLink, Section, useAction, useApi, useUi, useValidation } from "../components/ui";
import { MAIL_STATUS, formatDate, formatSize, formatTime } from "../format";
import { useOps } from "../realtime";
import { lightTheme } from "../theme";
import type { DraftInput, FileDto, MailDetailDto, MailDraftDto, MailSummaryDto, ProjectDto } from "../types";

const FILTERS: { value: string; label: string; query: string; keep?: (m: MailSummaryDto) => boolean }[] = [
  { value: "all", label: "전체", query: "" },
  // Same rule as 오늘 (New or NeedsReply without a project); the API filters one status only, so this filters here.
  // shortcut: only the newest 200 mails are checked, add an unlinked filter to GET /api/mail if older ones go missing.
  { value: "new", label: "미분류", query: "", keep: (m) => !m.projectId && (m.status === "New" || m.status === "NeedsReply") },
  { value: "reply", label: "답장 필요", query: "&status=NeedsReply" },
];

const EXTRACTED: Record<string, string> = {
  brand: "브랜드",
  project_type: "프로젝트 유형",
  period: "기간",
  venue: "장소",
  area: "면적",
  budget: "예산",
  deadline: "마감",
  contact: "담당자",
  missing_fields: "빠진 정보",
};

const useStyles = makeStyles({
  toolbar: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalM, flexWrap: "wrap" },
  search: { flexGrow: 1, maxWidth: "360px" },
  panes: {
    flexGrow: 1,
    minHeight: 0,
    display: "grid",
    gridTemplateColumns: "320px minmax(0, 1fr)",
    gap: tokens.spacingHorizontalL,
  },
  listPane: { overflowY: "auto", minHeight: 0 },
  item: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    width: "100%",
    textAlign: "start",
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    border: "none",
    borderLeft: `3px solid transparent`,
    background: "none",
    color: tokens.colorNeutralForeground1,
    font: "inherit",
    cursor: "pointer",
    ":hover": { backgroundColor: tokens.colorNeutralBackground1Hover },
    ":focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" },
  },
  selected: { backgroundColor: tokens.colorNeutralBackground1Selected },
  unread: { borderLeftColor: tokens.colorBrandStroke1, fontWeight: tokens.fontWeightSemibold },
  itemTop: { display: "flex", gap: tokens.spacingHorizontalS, alignItems: "baseline", "& > .fui-Badge, & > svg": { flexShrink: 0 } },
  detail: {
    minHeight: 0,
    display: "grid",
    gridTemplateColumns: "minmax(0, 1fr) 280px",
    gap: tokens.spacingHorizontalL,
    // Rows sized to their content: auto rows would share the pane's height and draw the reader over the AI panel.
    "@media (max-width: 1279px)": { gridTemplateColumns: "minmax(0, 1fr)", gridAutoRows: "max-content", alignContent: "start", overflowY: "auto" },
  },
  // Below 1280px the AI panel stacks under the reader and the whole detail scrolls as one.
  reader: {
    overflowY: "auto",
    minHeight: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    "@media (max-width: 1279px)": { overflowY: "visible" },
  },
  side: {
    overflowY: "auto",
    minHeight: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    "@media (max-width: 1279px)": { overflowY: "visible" },
  },
  frame: {
    width: "100%",
    minHeight: "420px",
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    // Mail HTML is authored for white paper, in both themes.
    backgroundColor: "#ffffff",
  },
  text: { margin: 0, whiteSpace: "pre-wrap", overflowWrap: "anywhere", fontFamily: "inherit" },
  attachments: { display: "flex", flexWrap: "wrap", gap: tokens.spacingHorizontalS },
  placeholder: { display: "flex", alignItems: "center", justifyContent: "center", color: tokens.colorNeutralForeground3 },
  menu: { maxHeight: "320px", overflowY: "auto" },
  form: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
  files: { display: "flex", flexDirection: "column", maxHeight: "160px", overflowY: "auto" },
});

// The last character may not be punctuation that ends a sentence around the address.
const URL_PART = /(https?:\/\/[^\s<>"]*[^\s<>".,;:!?)\]'])/;

/** Plain-text mail with its http(s) addresses as links that open in the browser. */
function linkify(text: string) {
  return text.split(URL_PART).map((part, i) =>
    i % 2 ? (
      <a key={i} href={part} target="_blank" rel="noopener noreferrer">
        {part}
      </a>
    ) : (
      part
    ),
  );
}

/** Menu that links a mail to a project; the AI's suggestion comes first. */
export function LinkProjectMenu({ mail, onLinked }: { mail: MailSummaryDto; onLinked: (m: MailSummaryDto) => void }) {
  const s = useStyles();
  const ui = useUi();
  const [open, setOpen] = useState(false);
  const projects = useApi<ProjectDto[]>(open ? "/api/projects?status=Active" : null);
  const { error, run } = useAction();
  const link = (projectId: string) =>
    void run(async () =>
      onLinked(
        await api<MailSummaryDto>(`/api/mail/${mail.id}/link`, { method: "POST", body: JSON.stringify({ projectId }) }),
      ),
    );
  const suggested = (p: ProjectDto) => p.id === mail.suggestedProjectId;
  const list = [...(projects.data ?? [])].sort((a, b) => Number(suggested(b)) - Number(suggested(a)));

  return (
    <>
      <Menu open={open} onOpenChange={(_, d) => setOpen(d.open)}>
        <MenuTrigger disableButtonEnhancement>
          <Button size="small" icon={<Link20Regular />}>
            프로젝트 연결
          </Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList className={s.menu}>
            {!projects.data &&
              (projects.error ? (
                <MenuItem persistOnClick onClick={projects.reload}>
                  {projects.error} 다시 시도
                </MenuItem>
              ) : (
                <MenuItem disabled>프로젝트를 불러오는 중입니다.</MenuItem>
              ))}
            {projects.data?.length === 0 && <MenuItem disabled>진행 중인 프로젝트가 없습니다.</MenuItem>}
            {list.map((p) => (
              <MenuItem key={p.id} secondaryContent={suggested(p) ? "추천" : undefined} onClick={() => link(p.id)}>
                {p.jobNo} {p.name}
              </MenuItem>
            ))}
          </MenuList>
        </MenuPopover>
      </Menu>
      {error && <Caption1 className={ui.error}>{error}</Caption1>}
    </>
  );
}

/** Closes a mail that needs nothing more (an ad, a reply sent by phone), or opens it again. */
export function MailDoneButton({ mail, onChanged }: { mail: MailSummaryDto; onChanged: (m: MailSummaryDto) => void }) {
  const ui = useUi();
  const { busy, error, run } = useAction();
  const done = mail.status === "Done";
  const set = () =>
    void run(async () =>
      onChanged(
        await api<MailSummaryDto>(`/api/mail/${mail.id}/status`, {
          method: "POST",
          body: JSON.stringify({ status: done ? "NeedsReply" : "Done" }),
        }),
      ),
    );
  return (
    <>
      <Button size="small" disabled={busy} onClick={set}>
        {done ? "답장 필요로 되돌리기" : "처리 완료"}
      </Button>
      {error && <Caption1 className={ui.error}>{error}</Caption1>}
    </>
  );
}

export function Mail() {
  const s = useStyles();
  const ui = useUi();
  const { id } = useParams();
  const navigate = useNavigate();
  const [filter, setFilter] = useState("all");
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const sync = useAction();
  const [syncNote, setSyncNote] = useState<string>();
  useEffect(() => {
    const t = setTimeout(() => setQuery(q.trim()), 300);
    return () => clearTimeout(t);
  }, [q]);

  const { query: status, keep } = FILTERS.find((f) => f.value === filter)!;
  const list = useApi<MailSummaryDto[]>(`/api/mail?take=200${status}${query ? `&q=${encodeURIComponent(query)}` : ""}`);
  const shown = keep && list.data ? { ...list, data: list.data.filter(keep) } : list;
  const projects = useApi<ProjectDto[]>("/api/projects");
  const replace = (m: MailSummaryDto) => list.update((l) => l.map((x) => (x.id === m.id ? m : x)));
  useOps({
    mailUpdated: (m) => (list.data?.some((x) => x.id === m.id) ? replace(m) : list.reload()),
    connected: list.reload,
  });

  const runSync = () =>
    void sync.run(async () => {
      const r = await api<{ added: number }>("/api/mail/sync", { method: "POST" });
      setSyncNote(r.added ? `새 메일 ${r.added}통을 가져왔습니다.` : "새 메일이 없습니다.");
      list.reload();
    });

  const onKey = (e: KeyboardEvent, mails: MailSummaryDto[]) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    e.preventDefault();
    const i = mails.findIndex((m) => m.id === id);
    const next = mails[Math.min(mails.length - 1, Math.max(0, i + (e.key === "ArrowDown" ? 1 : -1)))];
    if (!next) return;
    navigate(`/mail/${next.id}`);
    document.getElementById(`mail-${next.id}`)?.focus();
  };

  return (
    <Page
      fill
      title="메일"
      description="받은 메일을 프로젝트에 연결하고, 답장하거나 AI에게 작업을 맡깁니다."
      actions={
        <>
          {syncNote && <Caption1 aria-live="polite">{syncNote}</Caption1>}
          <Button icon={<ArrowSync20Regular />} disabled={sync.busy} onClick={runSync}>
            메일 가져오기
          </Button>
        </>
      }
    >
      <div className={s.toolbar}>
        <TabList selectedValue={filter} onTabSelect={(_, d) => setFilter(String(d.value))}>
          {FILTERS.map((f) => (
            <Tab key={f.value} value={f.value}>
              {f.label}
            </Tab>
          ))}
        </TabList>
        <SearchBox
          className={s.search}
          placeholder="메일 검색"
          aria-label="메일 검색"
          value={q}
          onChange={(_, d) => setQ(d.value)}
        />
      </div>
      {sync.error && <ErrorBar message={sync.error} />}
      <div className={s.panes}>
        <div className={s.listPane}>
          <Async state={shown} rows={8}>
            {(mails) =>
              mails.length === 0 ? (
                <div className={ui.card}>
                  {query || filter !== "all" ? (
                    <EmptyState
                      title="조건에 맞는 메일이 없습니다"
                      body="검색어나 필터를 바꿔 보세요."
                      action={
                        <Button
                          onClick={() => {
                            setQ("");
                            setFilter("all");
                          }}
                        >
                          전체 메일 보기
                        </Button>
                      }
                    />
                  ) : (
                    <EmptyState
                      title="받은 메일이 없습니다"
                      body="NAVER WORKS에서 메일을 가져오면 여기에 표시됩니다."
                      action={<Button onClick={runSync}>메일 가져오기</Button>}
                    />
                  )}
                </div>
              ) : (
                <ul className={ui.list} aria-label="메일 목록" onKeyDown={(e) => onKey(e, mails)}>
                  {mails.map((m) => (
                    <li key={m.id}>
                      <button
                        id={`mail-${m.id}`}
                        type="button"
                        aria-current={m.id === id || undefined}
                        className={mergeClasses(s.item, m.id === id && s.selected, m.status === "New" && s.unread)}
                        onClick={() => navigate(`/mail/${m.id}`)}
                      >
                        <span className={s.itemTop}>
                          <Text weight={m.status === "New" ? "semibold" : "regular"} className={ui.ellipsis} style={{ flexGrow: 1 }}>
                            {m.fromName ?? m.fromAddress}
                          </Text>
                          <Caption1 className={ui.muted}>{formatDate(m.receivedAt)}</Caption1>
                        </span>
                        <Body1 className={ui.ellipsis}>{m.subject}</Body1>
                        <span className={s.itemTop}>
                          <Caption1 className={mergeClasses(ui.muted, ui.ellipsis)} style={{ flexGrow: 1 }}>
                            {m.summary ?? ""}
                          </Caption1>
                          {m.hasAttachments && <Attach16Regular aria-label="첨부 있음" />}
                          {m.hasDraft && (
                            <Badge appearance="tint" shape="rounded" size="small" color="informative">
                              초안 있음
                            </Badge>
                          )}
                          {m.jobNo ? (
                            <Badge appearance="outline" shape="rounded" size="small">
                              {m.jobNo}
                            </Badge>
                          ) : (
                            <Badge appearance="tint" shape="rounded" size="small" color="warning">
                              {MAIL_STATUS[m.status]}
                            </Badge>
                          )}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )
            }
          </Async>
        </div>
        {id ? (
          <MailReader
            key={id}
            id={id}
            projects={projects.data ?? []}
            onChanged={replace}
            onProjectCreated={projects.reload}
          />
        ) : (
          <div className={s.placeholder}>왼쪽 목록에서 메일을 고르세요.</div>
        )}
      </div>
    </Page>
  );
}

function MailReader({
  id,
  projects,
  onChanged,
  onProjectCreated,
}: {
  id: string;
  projects: ProjectDto[];
  onChanged: (m: MailSummaryDto) => void;
  onProjectCreated: () => void;
}) {
  const s = useStyles();
  const ui = useUi();
  const detail = useApi<MailDetailDto>(`/api/mail/${id}`);
  const [replying, setReplying] = useState(false);
  const [jobOpen, setJobOpen] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);
  const save = useAction();
  const [saved, setSaved] = useState<string>();

  const changed = (m: MailSummaryDto) => {
    onChanged(m);
    detail.reload();
  };

  return (
    <Async state={detail} rows={8}>
      {(m) => {
        const project = projects.find((p) => p.id === m.projectId);
        const suggested = projects.find((p) => p.id === m.suggestedProjectId);
        const extracted = Object.entries(m.extracted ?? {}).filter(([, v]) => v != null && v.length > 0);
        const ex = (k: string) => {
          const v = m.extracted?.[k];
          return (Array.isArray(v) ? v.join(", ") : v) || null;
        };
        const seed: ProjectSeed = {
          name: [ex("brand"), ex("project_type")].filter(Boolean).join(" ") || m.subject,
          clientName: ex("brand"),
          venue: ex("venue"),
          stage: "Inquiry",
        };
        const saveAttachment = (index: number) =>
          void save.run(async () => {
            const f = await api<FileDto>(`/api/mail/${m.id}/attachments/${index}/save`, { method: "POST" });
            setSaved(`프로젝트 폴더에 저장했습니다: ${f.relativePath}`);
          });

        return (
          <div className={s.detail}>
            <article className={s.reader} aria-label="메일 본문">
              <div>
                <Subtitle1 as="h2">{m.subject}</Subtitle1>
                <dl className={ui.dl} style={{ marginTop: tokens.spacingVerticalS }}>
                  <dt>보낸 사람</dt>
                  <dd>
                    {m.fromName ? `${m.fromName} <${m.fromAddress}>` : m.fromAddress}
                  </dd>
                  <dt>받는 사람</dt>
                  <dd>{m.to}</dd>
                  {m.cc && (
                    <>
                      <dt>참조</dt>
                      <dd>{m.cc}</dd>
                    </>
                  )}
                  <dt>받은 시각</dt>
                  <dd>
                    {formatDate(m.receivedAt)} {formatTime(m.receivedAt)}
                  </dd>
                </dl>
              </div>
              {m.attachments.length > 0 && (
                <div className={s.attachments}>
                  {m.attachments.map((a) => (
                    <Button
                      key={a.index}
                      size="small"
                      icon={<Attach16Regular />}
                      title="프로젝트 00_IN 폴더에 저장"
                      disabled={save.busy}
                      onClick={() => saveAttachment(a.index)}
                    >
                      {a.fileName} ({formatSize(a.size)}) 저장
                    </Button>
                  ))}
                </div>
              )}
              {saved && <Caption1 aria-live="polite">{saved}</Caption1>}
              {save.error && <ErrorBar message={save.error} />}
              {m.htmlBody ? (
                // The sandbox blocks scripts, forms and navigation from the mail HTML; its links (target=_blank) open in the browser.
                // The app's font, which the mail's own styles override; without it the frame falls back to a serif.
                <iframe
                  sandbox="allow-popups allow-popups-to-escape-sandbox"
                  srcDoc={`<style>body{font-family:${lightTheme.fontFamilyBase}}</style>${m.htmlBody}`}
                  title="메일 본문"
                  className={s.frame}
                />
              ) : (
                <pre className={s.text}>{linkify(m.textBody ?? "")}</pre>
              )}
              {m.thread.length > 1 && (
                <Section title="같은 대화">
                  <ul className={ui.list}>
                    {m.thread.map((t) => (
                      <li key={t.id} className={ui.row}>
                        {t.id === m.id ? (
                          <Text weight="semibold" className={ui.grow}>
                            {t.subject}
                          </Text>
                        ) : (
                          <RouterLink to={`/mail/${t.id}`} className={ui.grow}>
                            {t.subject}
                          </RouterLink>
                        )}
                        <Caption1 className={ui.muted}>
                          {t.fromName ?? t.fromAddress} · {formatDate(t.receivedAt)}
                        </Caption1>
                      </li>
                    ))}
                  </ul>
                </Section>
              )}
              {replying && (
                <ReplyEditor mail={m} project={project} onClose={() => setReplying(false)} onSent={detail.reload} />
              )}
            </article>
            <aside className={s.side} aria-label="AI 정리">
              <div className={ui.actions}>
                <LinkProjectMenu mail={m} onLinked={changed} />
                <Button size="small" onClick={() => setCreateOpen(true)}>
                  새 프로젝트 만들기
                </Button>
                <Button size="small" appearance="primary" onClick={() => setReplying(true)}>
                  답장 쓰기
                </Button>
                <Button size="small" onClick={() => setJobOpen(true)}>
                  작업 맡기기
                </Button>
                <MailDoneButton mail={m} onChanged={changed} />
              </div>
              <Section title="요약">
                <Body1>{m.summary ?? "아직 AI가 정리하지 않은 메일입니다."}</Body1>
              </Section>
              <Section title="추출 정보">
                {extracted.length === 0 ? (
                  <Body1 className={ui.muted}>추출한 정보가 없습니다.</Body1>
                ) : (
                  <dl className={ui.dl}>
                    {extracted.map(([k, v]) => (
                      <div key={k} style={{ display: "contents" }}>
                        <dt>{EXTRACTED[k] ?? k}</dt>
                        <dd>{Array.isArray(v) ? v.join(", ") : v}</dd>
                      </div>
                    ))}
                  </dl>
                )}
              </Section>
              <Section title="추천 프로젝트">
                {project ? (
                  <Body1>
                    연결됨: <RouterLink to={`/projects/${project.id}`}>{`${project.jobNo} ${project.name}`}</RouterLink>
                  </Body1>
                ) : suggested ? (
                  <Body1>
                    <RouterLink to={`/projects/${suggested.id}`}>{`${suggested.jobNo} ${suggested.name}`}</RouterLink>
                    {m.suggestedConfidence != null && ` (확신도 ${Math.round(m.suggestedConfidence * 100)}%)`}
                  </Body1>
                ) : (
                  <Body1 className={ui.muted}>추천할 프로젝트가 없습니다. 새 프로젝트를 만들 수 있습니다.</Body1>
                )}
              </Section>
            </aside>
            <NewJobDialog
              open={jobOpen}
              onClose={() => setJobOpen(false)}
              mail={m}
              projectId={m.projectId}
              stage={project?.stage}
            />
            <ProjectForm
              open={createOpen}
              title="새 프로젝트 만들기"
              seed={seed}
              onClose={() => setCreateOpen(false)}
              onSubmit={async (input) => {
                await api<ProjectDto>(`/api/mail/${m.id}/create-project`, { method: "POST", body: JSON.stringify(input) });
                onProjectCreated();
                detail.reload();
              }}
            />
          </div>
        );
      }}
    </Async>
  );
}

function ReplyEditor({
  mail,
  project,
  onClose,
  onSent,
}: {
  mail: MailDetailDto;
  project?: ProjectDto;
  onClose: () => void;
  onSent: () => void;
}) {
  const s = useStyles();
  const ui = useUi();
  const drafts = useApi<MailDraftDto[]>(`/api/mail/drafts?mailId=${mail.id}`);
  const files = useApi<FileDto[]>(project ? `/api/projects/${project.id}/files` : null);
  const { busy, error, run } = useAction();
  const [draftId, setDraftId] = useState<string>();
  const [note, setNote] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [v, setV] = useState<DraftInput>(() => ({
    replyToMailId: mail.id,
    projectId: mail.projectId,
    to: mail.fromAddress,
    cc: "",
    subject: /^re:/i.test(mail.subject) ? mail.subject : `RE: ${mail.subject}`,
    body: "",
    attachments: [],
  }));

  // An unsent draft (the AI's from the 회신 초안 job, or one saved earlier) is picked up once.
  const loaded = useRef(false);
  useEffect(() => {
    if (loaded.current || !drafts.data) return;
    loaded.current = true;
    const d = drafts.data.filter((x) => x.status === "draft").sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];
    if (!d) return;
    setDraftId(d.id);
    setV({
      replyToMailId: d.replyToMailId,
      projectId: d.projectId,
      to: d.to,
      cc: d.cc ?? "",
      subject: d.subject,
      body: d.body,
      attachments: d.attachments,
    });
    setNote(d.author === "ai" ? "AI가 쓴 초안을 불러왔습니다. 확인 후 보내 주세요." : "저장한 초안을 불러왔습니다.");
  }, [drafts.data]);

  const val = useValidation({
    to: v.to.trim() ? undefined : "받는 사람을 입력해 주세요.",
    subject: v.subject.trim() ? undefined : "제목을 입력해 주세요.",
    body: v.body.trim() ? undefined : "본문을 입력해 주세요.",
  });
  const field = (name: "to" | "cc" | "subject" | "body") => ({
    id: val.id(name),
    value: v[name] ?? "",
    onBlur: val.blur(name),
    onChange: (_: unknown, d: { value: string }) => {
      val.edit(name);
      setV((x) => ({ ...x, [name]: d.value }));
    },
  });

  const persist = async () => {
    const body = JSON.stringify({ ...v, cc: v.cc?.trim() || null });
    const d = draftId
      ? await api<MailDraftDto>(`/api/mail/drafts/${draftId}`, { method: "PUT", body })
      : await api<MailDraftDto>("/api/mail/drafts", { method: "POST", body });
    setDraftId(d.id);
    return d;
  };
  const saveDraft = () => {
    if (!val.check()) return;
    void run(async () => {
      await persist();
      setNote("초안을 저장했습니다.");
    });
  };
  // The dialog stays open with its button disabled until the send ends, so a second click cannot send twice.
  const send = () =>
    void run(async () => {
      const d = await persist();
      await api(`/api/mail/drafts/${d.id}/send`, { method: "POST" });
      onSent();
      onClose();
    }).then(() => setConfirming(false));

  const toggleFile = (path: string, on: boolean) =>
    setV((x) => ({ ...x, attachments: on ? [...x.attachments, path] : x.attachments.filter((a) => a !== path) }));

  return (
    <Section title="답장">
      <div className={mergeClasses(ui.card, s.form)}>
        {note && <Caption1 aria-live="polite">{note}</Caption1>}
        <Field label="받는 사람" required validationMessage={val.message("to")}>
          <Input {...field("to")} />
        </Field>
        <Field label="참조 (선택)">
          <Input {...field("cc")} />
        </Field>
        <Field label="제목" required validationMessage={val.message("subject")}>
          <Input {...field("subject")} />
        </Field>
        <Field label="본문" required validationMessage={val.message("body")}>
          <Textarea rows={10} resize="vertical" {...field("body")} />
        </Field>
        <Field label="첨부 (프로젝트 파일)">
          {!project ? (
            <Caption1 className={ui.muted}>메일을 프로젝트에 연결하면 프로젝트 파일을 첨부할 수 있습니다.</Caption1>
          ) : (
            <Async state={files} rows={2}>
              {(list) =>
                list.length === 0 ? (
                  <Caption1 className={ui.muted}>프로젝트 폴더에 첨부할 파일이 없습니다.</Caption1>
                ) : (
                  <div className={s.files}>
                    {list.map((f) => {
                      const path = joinPath(project.folderPath, f.relativePath);
                      return (
                        <Checkbox
                          key={f.relativePath}
                          label={f.relativePath}
                          checked={v.attachments.includes(path)}
                          onChange={(_, d) => toggleFile(path, d.checked === true)}
                        />
                      );
                    })}
                  </div>
                )
              }
            </Async>
          )}
        </Field>
        {drafts.error && <ErrorBar message={drafts.error} onRetry={drafts.reload} />}
        {error && <ErrorBar message={error} />}
        {/* Until the saved drafts load, saving would create a second draft next to the AI's. */}
        <div className={ui.actions}>
          <Button appearance="primary" disabled={busy || !drafts.data} onClick={() => val.check() && setConfirming(true)}>
            보내기
          </Button>
          <Button disabled={busy || !drafts.data} onClick={saveDraft}>
            초안 저장
          </Button>
          <Button appearance="subtle" onClick={onClose}>
            닫기
          </Button>
        </div>
      </div>
      <Confirm
        open={confirming}
        title="메일을 보낼까요?"
        body={`이 메일을 보냅니다. 받는 사람: ${v.to.trim()}${v.cc?.trim() ? `, 참조: ${v.cc.trim()}` : ""}`}
        confirm="보내기"
        busy={busy}
        onConfirm={send}
        onCancel={() => setConfirming(false)}
      />
    </Section>
  );
}
