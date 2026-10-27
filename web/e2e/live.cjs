// API checks against the server scripts/e2e.sh starts: Codex health always; with E2E_LIVE=1 one real Claude Code brief job on a
// sample project (log in docs/screens/live-claude-job.txt) and the Claude engine check.
const fs = require("node:fs");
const path = require("node:path");

const { E2E_URL: base, E2E_TOKEN: token, E2E_LIVE: live } = process.env;
const hangul = /[가-힣]/;

const api = async (p, init = {}) => {
  const r = await fetch(base + p, { ...init, headers: { "X-Ofnr-Token": token, "Content-Type": "application/json" } });
  const body = await r.json().catch(() => null);
  if (!r.ok) throw new Error(`${p}: ${r.status} ${JSON.stringify(body)}`);
  return body;
};
const assert = (ok, message) => {
  if (!ok) throw new Error(message);
};

(async () => {
  const codex = (await api("/api/health")).engines.find((e) => e.name === "codex");
  assert(codex?.loggedIn === false && hangul.test(codex.detail ?? ""), `codex health: ${JSON.stringify(codex)}`);
  console.log(`codex: not logged in, "${codex.detail}"`);
  if (live !== "1") return;

  const project = (await api("/api/projects")).find((p) => p.name === "샘플 브랜드 쇼룸 리뉴얼");
  const job = await api("/api/jobs", {
    method: "POST",
    body: JSON.stringify({
      recipeId: "brief",
      projectId: project.id,
      engine: "claude",
      instruction: "점검용 샘플 프로젝트입니다. 대시보드 도구로 프로젝트 정보를 확인하고, 한 쪽 이내의 짧은 요구사항 정리 문서를 만드세요.",
    }),
  });
  const until = Date.now() + 10 * 60_000;
  let d;
  for (;;) {
    d = await api(`/api/jobs/${job.id}`);
    if (!["Queued", "Running"].includes(d.job.status)) break;
    assert(Date.now() < until, "brief job did not finish in 10 minutes");
    await new Promise((r) => setTimeout(r, 3000));
  }
  const run = d.runs.at(-1);
  fs.writeFileSync(
    path.resolve(__dirname, "../../docs/screens/live-claude-job.txt"),
    [
      `${d.job.title} (${d.job.recipeId}, ${d.job.engine} ${run?.engineVersion ?? "?"})`,
      `status ${d.job.status}, cost $${d.job.costUsd ?? 0}, run ${run?.startedAt} to ${run?.endedAt}${run?.error ? `, error ${run.error}` : ""}`,
      "",
      ...d.events.map((e) => `${String(e.seq).padStart(3)} ${e.kind.padEnd(7)} ${e.summary}`),
      "",
      `artifacts: ${d.artifacts.map((a) => `${a.relativePath} (${a.size} bytes)`).join(", ") || "none"}`,
      `result: ${JSON.stringify(d.result, null, 2)}`,
      "",
    ].join("\n"),
  );
  assert(d.job.status === "Review", `brief job ended ${d.job.status}: ${run?.error ?? ""}`);
  const brief = d.artifacts.find((a) => /^01_PLAN\/OFNR-\d{5}_BRIEF_v01\.md$/.test(a.relativePath));
  assert(brief && fs.existsSync(path.join(project.folderPath, brief.relativePath)), "no OFNR-*_BRIEF_v01.md in the project folder");
  assert(d.events.some((e) => e.kind === "tool" && e.summary.startsWith("mcp__ofnr__get_project")), "no mcp__ofnr__get_project tool event");
  console.log(`claude job: ${brief.relativePath}, $${d.job.costUsd}`);

  const check = await api("/api/health/engine-check/claude", { method: "POST" });
  assert(check.ok && check.loggedIn, `claude engine check: ${JSON.stringify(check)}`);
  console.log(`claude engine check: ${check.detail ?? "ok"}`);
})().catch((e) => {
  console.error(e.message ?? e);
  process.exit(1);
});
