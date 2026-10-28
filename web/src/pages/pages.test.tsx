// @vitest-environment jsdom
import { useState, type ReactElement } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OpsHandlers } from "../realtime";
import type {
  AgentsDto,
  HealthDto,
  JobDetailDto,
  JobDto,
  JobEventDto,
  MailDetailDto,
  MailDraftDto,
  MailSummaryDto,
  ProjectDto,
  RecipeDto,
  SettingsDto,
  TodayDto,
} from "../types";
import { ApprovalCard } from "../components/ApprovalCard";
import { JobLog } from "../components/JobLog";
import { NewJobDialog } from "../components/NewJobDialog";
import { ProjectForm, toInput } from "../components/ProjectForm";
import { Agents } from "./Agents";
import { Board } from "./Board";
import { JobDetail } from "./JobDetail";
import { Mail } from "./Mail";
import { Settings } from "./Settings";
import { Today } from "./Today";

const ops = vi.hoisted(() => ({ last: {} as OpsHandlers, joinJob: vi.fn(async () => undefined) }));
vi.mock("../realtime", () => ({
  useOps: (h: OpsHandlers) => {
    ops.last = h;
  },
  joinJob: ops.joinJob,
  leaveJob: async () => undefined,
}));

// Off by default; the update test turns Tauri on.
const tauri = vi.hoisted(() => ({ on: false, invoke: vi.fn() }));
vi.mock("@tauri-apps/api/core", () => ({ isTauri: () => tauri.on, invoke: tauri.invoke }));
const updater = vi.hoisted(() => ({ check: vi.fn() }));
vi.mock("@tauri-apps/plugin-updater", () => updater);

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

interface Call { method: string; path: string; body: unknown }

/**
 * Routes are "METHOD /path"; a GET with a query string also matches its route without the query.
 * A function route runs on each call and returns the body, so a test can hold or count a request.
 */
function mockApi(routes: Record<string, unknown>) {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? "GET";
    calls.push({ method, path: url, body: typeof init.body === "string" ? JSON.parse(init.body) : undefined });
    const key = `${method} ${url}`;
    const route = key in routes ? routes[key] : routes[key.split("?")[0]];
    const hit = typeof route === "function" ? await route() : route;
    return hit === undefined
      ? new Response(JSON.stringify({ error: "없는 경로입니다." }), { status: 404 })
      : new Response(JSON.stringify(hit), { status: 200 });
  });
  return calls;
}

function renderAt(ui: ReactElement, path = "/", route = "*") {
  return render(
    <FluentProvider theme={webLightTheme}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path={route} element={ui} />
        </Routes>
      </MemoryRouter>
    </FluentProvider>,
  );
}

const project: ProjectDto = {
  id: "p1",
  jobNo: "OFNR-26031",
  name: "성수 팝업",
  slug: "TNF-Seongsu",
  clientId: "c1",
  clientName: "TNF",
  stage: "Design",
  status: "Active",
  venue: "성수동",
  installDate: "2026-10-30",
  openDate: "2026-11-01",
  endDate: "2026-11-15",
  teardownDate: "2026-11-16",
  quoteAmount: 12000000,
  contractAmount: null,
  costAmount: null,
  folderPath: "D:/OFNR/Projects/2026/OFNR-26031_TNF-Seongsu",
  attentionCount: 1,
  runningCount: 0,
  instructions: null,
};

const job = (over: Partial<JobDto> = {}): JobDto => ({
  id: "j1",
  projectId: "p1",
  jobNo: "OFNR-26031",
  recipeId: "quote",
  recipeName: "견적서",
  title: "견적서 · 성수 팝업",
  engine: "claude",
  status: "Running",
  createdAt: "2026-10-27T01:00:00Z",
  updatedAt: "2026-10-27T01:05:00Z",
  costUsd: 0.42,
  summary: "단가표를 읽는 중",
  ...over,
});

const mail: MailSummaryDto = {
  id: "m1",
  fromName: "김담당",
  fromAddress: "kim@client.co.kr",
  subject: "성수 팝업 견적 요청드립니다",
  receivedAt: "2026-10-27T00:30:00Z",
  status: "NeedsReply",
  category: "inquiry",
  summary: "11월 성수 팝업 견적 요청",
  projectId: "p1",
  jobNo: "OFNR-26031",
  suggestedProjectId: null,
  suggestedConfidence: null,
  hasAttachments: false,
  hasDraft: false,
};

const event = (id: number, kind: JobEventDto["kind"], summary: string, jobId = "j1"): JobEventDto => ({
  id,
  jobId,
  runId: "r1",
  seq: id,
  kind,
  summary,
  at: "2026-10-27T01:00:00Z",
});

const mailDetail: MailDetailDto = {
  ...mail,
  to: "ceo@ofnr.co.kr",
  cc: null,
  textBody: "견적 부탁드립니다.",
  htmlBody: null,
  attachments: [],
  extracted: { brand: "TNF", venue: "성수동" },
  thread: [],
};

const draft: MailDraftDto = {
  id: "d1",
  replyToMailId: "m1",
  projectId: "p1",
  to: "kim@client.co.kr",
  cc: null,
  subject: "RE: 성수 팝업 견적 요청드립니다",
  body: "안녕하세요",
  attachments: [],
  author: "ceo",
  status: "draft",
  createdAt: "2026-10-27T02:00:00Z",
  sentAt: null,
};

const mailRoutes: Record<string, unknown> = {
  "GET /api/mail": [mail],
  "GET /api/mail/m1": mailDetail,
  "GET /api/projects": [project],
  "GET /api/projects/p1/files": [],
  "GET /api/mail/drafts": [],
  "POST /api/mail/drafts": draft,
  "POST /api/mail/drafts/d1/send": { ...draft, status: "sent" },
};

/** Opens the reply editor on mail m1 with a body typed in. */
async function openReply() {
  renderAt(<Mail />, "/mail/m1", "/mail/:id");
  fireEvent.click(await screen.findByRole("button", { name: "답장 쓰기" }));
  fireEvent.change(await screen.findByLabelText(/^본문/), { target: { value: "안녕하세요" } });
}

const settingsDto: SettingsDto = {
  dataRoot: "C:/OFNR",
  projectsRoot: "D:/OFNR/Projects",
  jobsRoot: "C:/OFNR/jobs",
  pluginDir: "C:/OFNR/claude-plugin",
  templatesDir: "C:/OFNR/templates",
  backupsDir: "C:/OFNR/backups",
  port: 47821,
  claudeExe: "claude",
  codexExe: "codex",
  defaultEngine: "claude",
  maxConcurrentJobs: 2,
  mailSyncMinutes: 5,
  autoTriage: true,
  appendSentCopy: true,
  claudeModel: null,
  codexModel: null,
  pgDumpPath: "pg_dump",
  mail: { address: "ceo@ofnr.co.kr", imapHost: "imap.worksmobile.com", imapPort: 993, smtpHost: "smtp.worksmobile.com", smtpPort: 465, sentFolder: null },
};

const ok = (name: string) => ({ name, ok: true, detail: null });
const healthDto: HealthDto = {
  version: "0.1.0",
  dataRoot: "C:/OFNR",
  projectsRoot: "D:/OFNR/Projects",
  db: ok("ofnr_ops"),
  mail: ok("ceo@ofnr.co.kr"),
  engines: [{ name: "claude", path: "C:/claude.exe", version: "2.1.311", pinned: "2.1.311", ok: true, loggedIn: false, detail: null }],
  mcp: [],
  paths: [],
};

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  vi.unstubAllGlobals();
  tauri.on = false;
});

describe("pages", () => {
  it("Today_shows_attention_cards_and_counts", async () => {
    const today: TodayDto = {
      counts: { attention: 3, running: 1, thisWeek: 2 },
      attention: [
        { kind: "approval", jobId: "j1", title: "엑셀 파일을 수정할까요?", detail: "Write 01_PLAN/x.xlsx", approvalId: "a1" },
        { kind: "review", jobId: "j2", title: "제안서 · 성수 팝업", detail: "결과물 2개", approvalId: null },
        { kind: "failed", jobId: "j3", title: "3D 모델 · 성수 팝업", detail: "작업이 실패했습니다: 시간 초과", approvalId: null },
      ],
      unsortedMail: [{ ...mail, projectId: null, jobNo: null }],
      upcoming: [{ projectId: "p1", jobNo: "OFNR-26031", projectName: "성수 팝업", kind: "install", date: "2026-10-30" }],
      running: [job()],
    };
    mockApi({ "GET /api/today": today });
    renderAt(<Today />);

    await screen.findByText("엑셀 파일을 수정할까요?");
    expect(screen.getByRole("group", { name: "확인 필요" }).textContent).toContain("3");
    expect(screen.getByRole("group", { name: "실행 중" }).textContent).toContain("1");
    expect(screen.getByRole("group", { name: "7일 안 일정" }).textContent).toContain("2");
    for (const name of ["승인", "수정 후 승인", "답변", "무시", "결과 보기", "다시 보기", "프로젝트 연결"]) {
      expect(screen.getByRole("button", { name }), name).toBeTruthy();
    }
    expect(screen.getByText("성수 팝업 견적 요청드립니다")).toBeTruthy();
    expect(screen.getByText("단가표를 읽는 중")).toBeTruthy();
  });

  it("Approval_accept_posts_decision", async () => {
    const calls = mockApi({ "POST /api/approvals/a1/decide": { id: "a1", status: "Decided", decision: "accept" } });
    const onDone = vi.fn();
    renderAt(
      <ApprovalCard
        item={{ kind: "approval", jobId: "j1", title: "엑셀 파일을 수정할까요?", detail: "Write x.xlsx", approvalId: "a1" }}
        onDone={onDone}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "승인" }));
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(calls).toContainEqual({ method: "POST", path: "/api/approvals/a1/decide", body: { decision: "accept" } });
  });

  it("Board_column_header_puts_total_on_its_own_line_in_every_column", async () => {
    mockApi({ "GET /api/projects": [project] });
    renderAt(<Board />);

    await screen.findByText("성수 팝업");
    const totals = screen.getAllByRole("heading", { level: 2 }).map((h) => {
      const title = h.parentElement!;
      expect(title.textContent).toMatch(/^\S+\d+건$/);
      return title.nextElementSibling?.textContent;
    });
    expect(totals).toEqual(["0원", "0원", "12,000,000원", "0원", "0원", "0원", "0원"]);
  });

  it("Question_card_answers_with_an_option_and_today_shows_the_confirmation", async () => {
    const question = {
      kind: "approval" as const,
      jobId: "j1",
      title: "견적서 · 성수 팝업",
      detail: JSON.stringify({ question: "부가세를 포함할까요?", options: ["포함", "별도", "포함"] }),
      approvalId: "a1",
      approvalKind: "question",
    };
    let loads = 0;
    const calls = mockApi({
      "GET /api/today": () => ({
        counts: { attention: loads === 0 ? 1 : 0, running: 0, thisWeek: 0 },
        attention: loads++ === 0 ? [question] : [],
        unsortedMail: [],
        upcoming: [],
        running: [],
      }),
      "POST /api/approvals/a1/decide": { id: "a1", status: "Decided", decision: "accept" },
    });
    vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ["setTimeout", "clearTimeout"] });
    const { container } = renderAt(<Today />);

    const card = await screen.findByRole("article", { name: "견적서 · 성수 팝업" });
    expect(within(card).getByText("AI 질문")).toBeTruthy();
    expect(within(card).getByText("부가세를 포함할까요?")).toBeTruthy();
    for (const name of ["포함", "별도", "답변 보내기", "무시"]) expect(within(card).getAllByRole("button", { name })).toHaveLength(1);
    expect(within(card).queryByRole("button", { name: "승인" })).toBeNull();
    // Mounted empty before the answer, so screen readers announce the text when it arrives.
    const live = container.querySelector('[aria-live="polite"]')!;
    expect(live.textContent).toBe("");

    fireEvent.click(within(card).getByRole("button", { name: "별도" }));
    await waitFor(() => expect(live.textContent).toBe("답변을 보냈습니다. 같은 작업이 이어서 실행됩니다."));
    await waitFor(() => expect(screen.queryByRole("article", { name: "견적서 · 성수 팝업" })).toBeNull());
    expect(calls).toContainEqual({ method: "POST", path: "/api/approvals/a1/decide", body: { decision: "accept", text: "별도" } });

    act(() => void vi.advanceTimersByTime(10_000));
    expect(live.textContent).toBe("");
    expect(container.querySelector('[aria-live="polite"]')).toBe(live);
  });

  it("Question_card_sends_typed_answer_only_on_ctrl_enter_outside_ime_and_ignore_closes", async () => {
    const calls = mockApi({ "POST /api/approvals/a1/decide": { id: "a1", status: "Decided" } });
    const onDone = vi.fn();
    const item = { kind: "approval" as const, jobId: "j1", title: "견적서", detail: "단가표가 두 개입니다. 어느 쪽을 쓸까요?", approvalId: "a1", approvalKind: "question" };
    renderAt(<ApprovalCard item={item} onDone={onDone} />);

    expect(screen.getByText("단가표가 두 개입니다. 어느 쪽을 쓸까요?")).toBeTruthy();
    const box = screen.getByLabelText(/^답변/);
    fireEvent.change(box, { target: { value: "2026년 단가표" } });
    fireEvent.keyDown(box, { key: "Enter" });
    fireEvent.keyDown(box, { key: "Enter", ctrlKey: true, isComposing: true });
    fireEvent.keyDown(box, { key: "Enter", ctrlKey: true, keyCode: 229 });
    // api() awaits server info before fetch, so let a wrongly sent request reach the mock first.
    await act(() => new Promise((r) => setTimeout(r, 0)));
    expect(calls).toEqual([]);
    expect(onDone).not.toHaveBeenCalled();

    expect(fireEvent.keyDown(box, { key: "Enter", ctrlKey: true, keyCode: 13 })).toBe(false);
    await waitFor(() => expect(onDone).toHaveBeenCalledWith("답변을 보냈습니다. 같은 작업이 이어서 실행됩니다."));
    expect(calls).toEqual([{ method: "POST", path: "/api/approvals/a1/decide", body: { decision: "respond", text: "2026년 단가표" } }]);

    cleanup();
    renderAt(<ApprovalCard item={item} onDone={onDone} />);
    fireEvent.click(screen.getByRole("button", { name: "무시" }));
    await waitFor(() => expect(onDone).toHaveBeenLastCalledWith("질문을 닫았습니다."));
    expect(calls[1].body).toEqual({ decision: "ignore" });
  });

  it("Question_card_shows_a_question_that_fits_the_title_once", () => {
    const title = "현장 사진이 없는데 도면 기준으로 설치 위치를 정해서 견적을 계속 진행해도 될까요?";
    for (const detail of ["", JSON.stringify({ question: title, options: [] })]) {
      renderAt(<ApprovalCard item={{ kind: "approval", jobId: "j1", title, detail, approvalId: "a1", approvalKind: "question" }} />);
      // The title wraps instead of being cut, so it is the only copy.
      expect(screen.getAllByText(title)).toHaveLength(1);
      cleanup();
    }
  });

  it("JobDetail_shows_pending_question_as_ai_question_and_confirms_the_answer", async () => {
    const detail: JobDetailDto = {
      job: job({ status: "NeedsAttention" }),
      runs: [],
      events: [],
      approvals: [
        { id: "a1", jobId: "j1", kind: "question", title: "확인 요청", detail: "현장 사진이 있나요?", status: "Pending", decision: null, createdAt: "2026-10-27T01:10:00Z" },
      ],
      artifacts: [],
      feedback: [],
      result: null,
    };
    let loads = 0;
    const calls = mockApi({
      "GET /api/jobs/j1": () => (loads++ === 0 ? detail : { ...detail, approvals: [] }),
      "GET /api/projects/p1": { project, mails: [], jobs: [], artifacts: [], activity: [] },
      "GET /api/recipes": [{ id: "quote", name: "견적서", stage: "Planning", engines: ["claude"], lock: "office", needsProject: true, skills: ["quote"] }],
      "POST /api/approvals/a1/decide": { id: "a1", status: "Decided", decision: "respond" },
    });
    renderAt(<JobDetail />, "/jobs/j1", "/jobs/:id");

    const card = await screen.findByRole("article", { name: "확인 요청" });
    expect(within(card).getByText("AI 질문")).toBeTruthy();
    // 스킬에 반영 offers the skills of the job's recipe.
    expect(await screen.findByRole("option", { name: "quote" })).toBeTruthy();
    fireEvent.change(within(card).getByLabelText(/^답변/), { target: { value: "없습니다" } });
    fireEvent.click(within(card).getByRole("button", { name: "답변 보내기" }));

    expect(await screen.findByText("답변을 보냈습니다. 같은 작업이 이어서 실행됩니다.")).toBeTruthy();
    expect(calls).toContainEqual({ method: "POST", path: "/api/approvals/a1/decide", body: { decision: "respond", text: "없습니다" } });
  });

  it("Mail_send_requires_confirmation", async () => {
    const calls = mockApi(mailRoutes);
    renderAt(<Mail />, "/mail/m1", "/mail/:id");

    fireEvent.click(await screen.findByRole("button", { name: "답장 쓰기" }));
    const to = (await screen.findByLabelText(/받는 사람/)) as HTMLInputElement;
    expect(to.value).toBe("kim@client.co.kr");
    fireEvent.change(screen.getByLabelText(/^본문/), { target: { value: "안녕하세요" } });
    fireEvent.click(screen.getByRole("button", { name: "보내기" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("이 메일을 보냅니다. 받는 사람: kim@client.co.kr")).toBeTruthy();
    expect(calls.some((c) => c.method === "POST")).toBe(false);

    fireEvent.click(within(dialog).getByRole("button", { name: "보내기" }));
    await waitFor(() => expect(calls.some((c) => c.path === "/api/mail/drafts/d1/send")).toBe(true));
    expect(calls.find((c) => c.method === "POST" && c.path === "/api/mail/drafts")?.body).toMatchObject({
      replyToMailId: "m1",
      to: "kim@client.co.kr",
      subject: "RE: 성수 팝업 견적 요청드립니다",
      body: "안녕하세요",
    });
  });

  it("Board_renders_seven_columns_in_order", async () => {
    mockApi({ "GET /api/projects": [project] });
    renderAt(<Board />);

    await screen.findByText("성수 팝업");
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual([
      "문의",
      "기획",
      "디자인",
      "제작",
      "운영",
      "리포트",
      "완료",
    ]);
    expect(within(screen.getByRole("region", { name: "디자인" })).getByText("성수 팝업")).toBeTruthy();
  });

  it("JobLog_appends_events_and_collapses_tools", async () => {
    renderAt(<JobLog jobId="j1" events={[event(1, "text", "견적 항목을 정리합니다.")]} />);
    expect(ops.joinJob).toHaveBeenCalledWith("j1");

    act(() => {
      ops.last.jobEvent?.(event(2, "tool", 'mcp__ofnr__get_project {"id":"p1"}'));
      ops.last.jobEvent?.(event(3, "warning", "요청이 많아 다시 시도합니다."));
      ops.last.jobEvent?.(event(4, "text", "다른 작업의 로그", "j2"));
      ops.last.jobEvent?.(event(2, "tool", 'mcp__ofnr__get_project {"id":"p1"}'));
      // The same run and seq again, e.g. history reloaded after a reconnect.
      ops.last.jobEvent?.({ ...event(2, "tool", 'mcp__ofnr__get_project {"id":"p1"}'), id: 99 });
    });

    expect(screen.getByText("견적 항목을 정리합니다.")).toBeTruthy();
    expect(screen.getByText("요청이 많아 다시 시도합니다.")).toBeTruthy();
    expect(screen.queryByText("다른 작업의 로그")).toBeNull();
    const tools = document.querySelectorAll("details");
    expect(tools).toHaveLength(1);
    expect(tools[0].open).toBe(false);
    expect(tools[0].querySelector("summary")?.textContent).toContain('프로젝트 정보 읽기 {"id":"p1"}');
    expect(tools[0].querySelector("summary")?.textContent).not.toContain("mcp__");
    expect(tools[0].textContent).toContain('mcp__ofnr__get_project {"id":"p1"}');
  });

  it("NewJobDialog_hides_codex_for_claude_only_recipe", async () => {
    const recipes: RecipeDto[] = [
      { id: "quote", name: "견적서", stage: "Planning", engines: ["claude", "codex"], lock: "office", needsProject: true, skills: ["quote"] },
      { id: "model3d", name: "3D 모델", stage: "Design", engines: ["claude"], lock: "sketchup", needsProject: true, skills: ["sketchup"] },
      { id: "free", name: "자유 지시", stage: null, engines: ["claude", "codex"], lock: null, needsProject: false, skills: [] },
    ];
    mockApi({ "GET /api/recipes": recipes, "GET /api/settings": { defaultEngine: "codex" } });
    renderAt(<NewJobDialog open projectId="p1" stage="Design" onClose={() => undefined} />);

    expect(await screen.findByRole("radio", { name: "Claude Code" })).toBeTruthy();
    expect(screen.queryByRole("radio", { name: "Codex" })).toBeNull();
    const recipe = screen.getByLabelText(/작업 종류/) as HTMLSelectElement;
    expect([...recipe.options].map((o) => o.value)).toEqual(["model3d", "free"]);

    fireEvent.change(recipe, { target: { value: "free" } });
    expect((screen.getByRole("radio", { name: "Codex" }) as HTMLInputElement).checked).toBe(true);
  });

  it("Mail_send_confirm_cannot_send_twice", async () => {
    let release = () => {};
    const held = new Promise<void>((r) => (release = r));
    const calls = mockApi({ ...mailRoutes, "POST /api/mail/drafts": async () => (await held, draft) });
    await openReply();
    fireEvent.click(screen.getByRole("button", { name: "보내기" }));

    const confirm = within(await screen.findByRole("dialog")).getByRole("button", { name: "보내기" });
    fireEvent.click(confirm);
    await waitFor(() => expect((confirm as HTMLButtonElement).disabled).toBe(true));
    fireEvent.click(confirm);
    release();

    await waitFor(() => expect(calls.some((c) => c.path === "/api/mail/drafts/d1/send")).toBe(true));
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(2);
  });

  it("Mail_reply_waits_for_saved_drafts_and_offers_retry", async () => {
    const { "GET /api/mail/drafts": _, ...withoutDrafts } = mailRoutes;
    mockApi(withoutDrafts);
    await openReply();

    expect(await screen.findByRole("button", { name: "다시 시도" })).toBeTruthy();
    expect((screen.getByRole("button", { name: "보내기" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "초안 저장" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("Mail_unsorted_filter_lists_new_and_needs_reply_without_project", async () => {
    const unlinked = { projectId: null, jobNo: null };
    const calls = mockApi({
      "GET /api/mail": [
        mail,
        { ...mail, ...unlinked, id: "m2", subject: "새 문의", status: "New" },
        { ...mail, ...unlinked, id: "m3", subject: "AI가 답장 필요로 분류한 문의", status: "NeedsReply" },
        { ...mail, ...unlinked, id: "m4", subject: "끝난 메일", status: "Done" },
      ],
      "GET /api/projects": [project],
    });
    renderAt(<Mail />, "/mail", "/mail");
    await screen.findByText("새 문의");

    fireEvent.click(screen.getByRole("tab", { name: "미분류" }));
    await waitFor(() =>
      expect(within(screen.getByRole("list", { name: "메일 목록" })).getAllByRole("button").map((b) => b.id)).toEqual([
        "mail-m2",
        "mail-m3",
      ]),
    );
    expect(calls.some((c) => c.path.includes("status="))).toBe(false);
  });

  it("Board_create_after_projectUpdated_shows_one_card", async () => {
    const created: ProjectDto = { ...project, id: "p2", name: "새 팝업", stage: "Inquiry", clientId: null, clientName: null };
    mockApi({
      "GET /api/projects": [project],
      "GET /api/clients": [],
      // The server broadcasts projectUpdated before it answers the POST.
      "POST /api/projects": () => {
        act(() => ops.last.projectUpdated?.(created));
        return created;
      },
    });
    renderAt(<Board />);
    await screen.findByText("성수 팝업");

    fireEvent.click(screen.getByRole("button", { name: "새 프로젝트" }));
    fireEvent.change(await screen.findByLabelText(/프로젝트명/), { target: { value: "새 팝업" } });
    await waitFor(() => expect((screen.getByRole("button", { name: "저장" }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole("button", { name: "저장" }));

    await waitFor(() => expect(screen.queryByRole("button", { name: "저장" })).toBeNull());
    expect(within(screen.getByRole("region", { name: "문의" })).getAllByRole("article")).toHaveLength(1);
  });

  it("ProjectForm_edit_sends_back_instructions", async () => {
    mockApi({ "GET /api/clients": [{ id: "c1", name: "TNF", mailDomains: [] }] });
    const onSubmit = vi.fn(async () => undefined);
    renderAt(
      <ProjectForm
        open
        editing
        title="프로젝트 수정"
        seed={toInput({ ...project, instructions: "주말 설치 금지" })}
        onClose={() => undefined}
        onSubmit={onSubmit}
      />,
    );

    const save = screen.getByRole("button", { name: "저장" }) as HTMLButtonElement;
    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientId: "c1", instructions: "주말 설치 금지" }));
  });

  it("ProjectForm_edit_sets_status", async () => {
    mockApi({ "GET /api/clients": [{ id: "c1", name: "TNF", mailDomains: [] }] });
    const onSubmit = vi.fn(async () => undefined);
    renderAt(<ProjectForm open editing title="프로젝트 수정" seed={toInput(project)} onClose={() => undefined} onSubmit={onSubmit} />);

    fireEvent.change(screen.getByLabelText(/상태/), { target: { value: "Lost" } });
    const save = screen.getByRole("button", { name: "저장" }) as HTMLButtonElement;
    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ status: "Lost" })));
  });

  it("ProjectForm_retry_after_failed_save_does_not_create_client_twice", async () => {
    const calls = mockApi({ "GET /api/clients": [], "POST /api/clients": { id: "c-new", name: "노스페이스", mailDomains: [] } });
    const onSubmit = vi.fn().mockRejectedValueOnce(new Error("저장하지 못했습니다.")).mockResolvedValue(undefined);
    renderAt(
      <ProjectForm
        open
        title="새 프로젝트"
        seed={{ name: "성수 팝업", clientName: "노스페이스" }}
        onClose={() => undefined}
        onSubmit={onSubmit}
      />,
    );

    const save = screen.getByRole("button", { name: "저장" }) as HTMLButtonElement;
    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);
    await screen.findByText("저장하지 못했습니다.");
    fireEvent.click(save);

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit).toHaveBeenLastCalledWith(expect.objectContaining({ clientId: "c-new" }));
    expect(calls.filter((c) => c.method === "POST" && c.path === "/api/clients")).toHaveLength(1);
  });

  it("NewJobDialog_reopens_with_the_chip_recipe_and_never_offers_skill_learning", async () => {
    const recipes: RecipeDto[] = [
      { id: "quote", name: "견적서", stage: "Planning", engines: ["claude"], lock: "office", needsProject: true, skills: ["quote"] },
      { id: "proposal", name: "제안서", stage: "Planning", engines: ["claude"], lock: "office", needsProject: true, skills: ["proposal"] },
      { id: "document", name: "문서", stage: "Planning", engines: ["claude"], lock: null, needsProject: true, skills: ["document"] },
      { id: "skill-learning", name: "스킬 학습", stage: null, engines: ["claude", "codex"], lock: null, needsProject: false, skills: ["skill-learning"] },
      { id: "free", name: "자유 지시", stage: null, engines: ["claude", "codex"], lock: null, needsProject: false, skills: [] },
    ];
    mockApi({ "GET /api/recipes": recipes, "GET /api/settings": { defaultEngine: "claude" } });
    function Chips() {
      const [recipeId, setRecipeId] = useState<string>();
      return (
        <>
          <button onClick={() => setRecipeId("quote")}>견적서 칩</button>
          <button onClick={() => setRecipeId("document")}>문서 칩</button>
          <NewJobDialog open={!!recipeId} onClose={() => setRecipeId(undefined)} projectId="p1" stage="Planning" recipeId={recipeId} />
        </>
      );
    }
    renderAt(<Chips />);

    fireEvent.click(screen.getByText("견적서 칩"));
    const first = (await screen.findByLabelText(/작업 종류/)) as HTMLSelectElement;
    expect(first.value).toBe("quote");
    expect([...first.options].map((o) => o.value)).toEqual(["quote", "proposal", "document", "free"]);
    fireEvent.change(first, { target: { value: "proposal" } });
    fireEvent.change(screen.getByLabelText(/지시 내용/), { target: { value: "A안으로" } });
    fireEvent.click(screen.getByRole("button", { name: "취소" }));
    await waitFor(() => expect(screen.queryByLabelText(/작업 종류/)).toBeNull());

    fireEvent.click(screen.getByText("문서 칩"));
    expect(((await screen.findByLabelText(/작업 종류/)) as HTMLSelectElement).value).toBe("document");
    expect((screen.getByLabelText(/지시 내용/) as HTMLTextAreaElement).value).toBe("");
  });

  it("Agents_empty_lists_have_an_action", async () => {
    const agents: AgentsDto = { running: [], queued: [], locks: [], costs: { monthUsd: 0, byRecipe: [] }, engines: [] };
    mockApi({ "GET /api/agents": agents, "GET /api/recipes": [] });
    renderAt(<Agents />);

    expect(await screen.findByText("앱 잠금이 없습니다")).toBeTruthy();
    expect(screen.getByText("엔진 정보가 없습니다")).toBeTruthy();
    expect(screen.getByText("이번 달에 실행한 작업이 없습니다")).toBeTruthy();
    expect(screen.getByRole("button", { name: "설정 열기" })).toBeTruthy();
  });

  it("Settings_rejects_empty_paths_and_bad_ports", async () => {
    const calls = mockApi({ "GET /api/settings": settingsDto });
    renderAt(<Settings />);

    const templates = await screen.findByLabelText(/회사 양식 폴더/);
    fireEvent.change(templates, { target: { value: " " } });
    fireEvent.blur(templates);
    expect(await screen.findByText("경로를 입력해 주세요.")).toBeTruthy();
    const imapPort = screen.getAllByLabelText(/^포트/)[0];
    fireEvent.change(imapPort, { target: { value: "" } });
    fireEvent.blur(imapPort);
    expect(await screen.findByText("1에서 65535 사이로 입력해 주세요.")).toBeTruthy();

    fireEvent.click(screen.getAllByRole("button", { name: "변경 저장" })[0]);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("Settings_saves_the_pg_dump_path", async () => {
    const pgDumpPath = "C:\\Program Files\\PostgreSQL\\17\\bin\\pg_dump.exe";
    const calls = mockApi({ "GET /api/settings": settingsDto, "PUT /api/settings": { ...settingsDto, pgDumpPath } });
    renderAt(<Settings />);

    fireEvent.change(await screen.findByLabelText(/pg_dump 실행 파일/), { target: { value: pgDumpPath } });
    fireEvent.click(screen.getAllByRole("button", { name: "변경 저장" })[0]);

    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ pgDumpPath }));
  });

  it("Settings_engine_check_updates_the_row", async () => {
    const calls = mockApi({
      "GET /api/settings": settingsDto,
      "GET /api/health": healthDto,
      "POST /api/health/engine-check/claude": { ...healthDto.engines[0], loggedIn: true },
    });
    renderAt(<Settings />);

    expect(await screen.findByText("로그인이 필요합니다.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "엔진 점검" }));
    await waitFor(() => expect(screen.queryByText("로그인이 필요합니다.")).toBeNull());
    expect(screen.getByText("Claude Code 2.1.311")).toBeTruthy();
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual(["/api/health/engine-check/claude"]);
  });

  it("Settings_update_waits_for_running_jobs_and_stops_the_server_after_download", async () => {
    tauri.on = true;
    const order: string[] = [];
    tauri.invoke.mockImplementation(async (cmd: string) => {
      if (cmd === "server_info") return { baseUrl: "", token: "t" };
      if (cmd === "autostart_enabled") return false;
      order.push(cmd);
    });
    let running: JobDto[] = [];
    let jobStartsDuringDownload = false;
    updater.check.mockResolvedValue({
      version: "0.2.0",
      download: async () => {
        order.push("download");
        if (jobStartsDuringDownload) running = [job()];
      },
      install: async () => void order.push("install"),
    });
    mockApi({ "GET /api/settings": settingsDto, "GET /api/health": healthDto, "GET /api/jobs": () => running });
    renderAt(<Settings />);

    const checkButton = (await screen.findByRole("button", { name: "업데이트 확인" })) as HTMLButtonElement;
    await waitFor(() => expect(checkButton.disabled).toBe(false));
    fireEvent.click(checkButton);
    const installButton = await screen.findByRole("button", { name: "지금 업데이트" });
    expect(screen.getByText("새 버전 0.2.0이 있습니다.")).toBeTruthy();

    running = [job()];
    fireEvent.click(installButton);
    expect(await screen.findByText("실행 중인 작업이 끝난 뒤에 업데이트해 주세요.")).toBeTruthy();
    expect(order).toEqual([]);

    const finishJob = async () => {
      await waitFor(() => expect((installButton as HTMLButtonElement).disabled).toBe(true));
      running = [];
      act(() => ops.last.jobUpdated?.(job({ status: "Done" })));
      await waitFor(() => expect((installButton as HTMLButtonElement).disabled).toBe(false));
    };
    await finishJob();

    jobStartsDuringDownload = true;
    fireEvent.click(installButton);
    await waitFor(() => expect(order).toEqual(["download"]));
    expect(await screen.findByText("실행 중인 작업이 끝난 뒤에 업데이트해 주세요.")).toBeTruthy();
    await finishJob();

    jobStartsDuringDownload = false;
    fireEvent.click(installButton);
    await waitFor(() => expect(order).toEqual(["download", "download", "stop_backend", "install"]));
  });

  it("ProjectForm_enter_that_commits_korean_ime_does_not_submit", async () => {
    mockApi({ "GET /api/clients": [] });
    renderAt(<ProjectForm open title="새 프로젝트" seed={{ name: "성수" }} onClose={() => undefined} onSubmit={async () => undefined} />);

    const name = await screen.findByLabelText(/프로젝트명/);
    expect(fireEvent.keyDown(name, { key: "Enter", keyCode: 229 })).toBe(false);
    expect(fireEvent.keyDown(name, { key: "Enter", isComposing: true })).toBe(false);
    expect(fireEvent.keyDown(name, { key: "Enter", keyCode: 13 })).toBe(true);
  });

  it("No_em_dash_in_source", () => {
    const sources = import.meta.glob<string>(["../**/*.ts", "../**/*.tsx", "../**/*.css"], {
      query: "?raw",
      import: "default",
      eager: true,
    });
    expect(Object.keys(sources).length).toBeGreaterThan(10);
    expect(Object.keys(sources).filter((f) => /[\u2013\u2014]/.test(sources[f]))).toEqual([]);
  });
});
