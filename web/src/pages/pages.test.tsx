// @vitest-environment jsdom
import type { ReactElement } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { MemoryRouter, Route, Routes } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OpsHandlers } from "../realtime";
import type { JobDto, JobEventDto, MailDetailDto, MailSummaryDto, ProjectDto, RecipeDto, TodayDto } from "../types";
import { ApprovalCard } from "../components/ApprovalCard";
import { JobLog } from "../components/JobLog";
import { NewJobDialog } from "../components/NewJobDialog";
import { Board } from "./Board";
import { Mail } from "./Mail";
import { Today } from "./Today";

const ops = vi.hoisted(() => ({ last: {} as OpsHandlers, joinJob: vi.fn(async () => undefined) }));
vi.mock("../realtime", () => ({
  useOps: (h: OpsHandlers) => {
    ops.last = h;
  },
  joinJob: ops.joinJob,
  leaveJob: async () => undefined,
}));

interface Call { method: string; path: string; body: unknown }

/** Routes are "METHOD /path"; a GET with a query string also matches its route without the query. */
function mockApi(routes: Record<string, unknown>) {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? "GET";
    calls.push({ method, path: url, body: typeof init.body === "string" ? JSON.parse(init.body) : undefined });
    const key = `${method} ${url}`;
    const hit = key in routes ? routes[key] : routes[key.split("?")[0]];
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

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
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
    expect(screen.getByRole("group", { name: "이번 주 일정" }).textContent).toContain("2");
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

  it("Mail_send_requires_confirmation", async () => {
    const detail: MailDetailDto = {
      ...mail,
      to: "ceo@ofnr.co.kr",
      cc: null,
      textBody: "견적 부탁드립니다.",
      htmlBody: null,
      attachments: [],
      extracted: { brand: "TNF", venue: "성수동" },
      thread: [],
    };
    const draft = {
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
    const calls = mockApi({
      "GET /api/mail": [mail],
      "GET /api/mail/m1": detail,
      "GET /api/projects": [project],
      "GET /api/projects/p1/files": [],
      "GET /api/mail/drafts": [],
      "POST /api/mail/drafts": draft,
      "POST /api/mail/drafts/d1/send": { ...draft, status: "sent" },
    });
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
    });

    expect(screen.getByText("견적 항목을 정리합니다.")).toBeTruthy();
    expect(screen.getByText("요청이 많아 다시 시도합니다.")).toBeTruthy();
    expect(screen.queryByText("다른 작업의 로그")).toBeNull();
    const tools = document.querySelectorAll("details");
    expect(tools).toHaveLength(1);
    expect(tools[0].open).toBe(false);
    expect(tools[0].querySelector("summary")?.textContent).toContain("mcp__ofnr__get_project");
  });

  it("NewJobDialog_hides_codex_for_claude_only_recipe", async () => {
    const recipes: RecipeDto[] = [
      { id: "quote", name: "견적서", stage: "Planning", engines: ["claude", "codex"], lock: "office", needsProject: true },
      { id: "model3d", name: "3D 모델", stage: "Design", engines: ["claude"], lock: "sketchup", needsProject: true },
      { id: "free", name: "자유 지시", stage: null, engines: ["claude", "codex"], lock: null, needsProject: false },
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
