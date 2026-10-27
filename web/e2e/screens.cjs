// Every view in light and dark at 1440x900 and 1024x768 against the server scripts/e2e.sh starts with --seed-demo:
// no console errors, no horizontal scroll in <main> (AppShell's scroll container: the document itself never scrolls), the mail
// reader never drawn over its AI panel, and a screenshot each in docs/screens/<scheme>-<width>-<view>.png, grown to the full content.
// Uses the globally installed Playwright (NODE_PATH=$(npm root -g)), so the web package has no browser dependency.
const { chromium } = require("playwright");
const fs = require("node:fs");
const path = require("node:path");

const { E2E_URL: base, E2E_TOKEN: token } = process.env;
const out = path.resolve(__dirname, "../../docs/screens");

// Console errors that are expected in this run: none so far.
const EXPECTED = [];

const get = async (p) => {
  const r = await fetch(base + p, { headers: { "X-Ofnr-Token": token } });
  if (!r.ok) throw new Error(`${p}: ${r.status}`);
  return r.json();
};

const click = (name, role = "button") => (page) => page.getByRole(role, { name, exact: true }).click();

(async () => {
  const [projects, mails, today, jobs] = await Promise.all([get("/api/projects"), get("/api/mail?take=200"), get("/api/today"), get("/api/jobs")]);
  const project = projects.find((p) => p.name === "샘플 성수 팝업스토어");
  const mail = mails.find((m) => m.status === "NeedsReply");
  const html = mails.find((m) => m.fromName === "최샘플");
  const review = today.attention.find((a) => a.kind === "review");
  const job = (recipeId) => jobs.find((j) => j.recipeId === recipeId).id;
  const tabs = { mail: "메일", jobs: "작업", files: "파일", activity: "활동", instructions: "지침" };
  // name: [route, a step before the checks]
  const views = {
    today: ["/"],
    mail: ["/mail"],
    "mail-detail": [`/mail/${mail.id}`],
    "mail-reply": [`/mail/${mail.id}`, click("답장 쓰기")],
    "mail-html": [`/mail/${html.id}`],
    projects: ["/projects"],
    "project-new": ["/projects", click("새 프로젝트")],
    "project-detail": [`/projects/${project.id}`],
    ...Object.fromEntries(Object.entries(tabs).map(([id, label]) => [`project-tab-${id}`, [`/projects/${project.id}`, click(label, "tab")]])),
    "project-job": [`/projects/${project.id}`, click("작업 맡기기")],
    "job-detail": [`/jobs/${review.jobId}`],
    "job-question": [`/jobs/${job("quote")}`],
    "job-failed": [`/jobs/${job("graphic")}`],
    "job-blocked": [`/jobs/${job("model3d")}`],
    agents: ["/agents"],
    skills: ["/skills"],
    settings: ["/settings"],
  };

  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch();
  const failures = [];
  for (const scheme of ["light", "dark"]) {
    for (const [width, height] of [[1440, 900], [1024, 768]]) {
      const context = await browser.newContext({ viewport: { width, height }, colorScheme: scheme, locale: "ko-KR" });
      for (const [name, [route, step]] of Object.entries(views)) {
        const page = await context.newPage();
        const errors = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(e.message));
        await page.goto(`${base}${route}?token=${token}`, { waitUntil: "networkidle" });
        if (step) {
          await step(page);
          await page.waitForLoadState("networkidle");
        }
        await page.waitForTimeout(400); // skeletons and dialogs fade in and out
        const id = `${scheme}-${width}-${name}`;

        const overflow = await page.evaluate(() => {
          const m = document.querySelector("main");
          return Math.max(m.scrollWidth - m.clientWidth, document.documentElement.scrollWidth - document.documentElement.clientWidth);
        });
        if (overflow > 0) failures.push(`${id}: page scrolls ${overflow}px horizontally`);
        const overlap = await page.evaluate(() => {
          const article = document.querySelector("article[aria-label='메일 본문']");
          const aside = document.querySelector("aside[aria-label='AI 정리']");
          if (!article || !aside) return null;
          const b = aside.getBoundingClientRect();
          const a = [article, ...article.children].map((e) => e.getBoundingClientRect())
            .find((a) => a.height > 0 && a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom);
          return a && `mail reader ${Math.round(a.top)}-${Math.round(a.bottom)}px drawn over the AI panel ${Math.round(b.top)}-${Math.round(b.bottom)}px`;
        });
        if (overlap) failures.push(`${id}: ${overlap}`);
        for (const e of errors.filter((e) => !EXPECTED.some((x) => x.test(e)))) failures.push(`${id}: console error: ${e}`);

        // Grow the viewport until <main> and the panes that fill it (not fixed-height boxes like the job log) stop scrolling.
        for (let i = 0, h = height; i < 3; i++) {
          const extra = await page.evaluate(() =>
            Math.max(
              0,
              ...[document.querySelector("main"), ...document.querySelectorAll("main *")]
                .filter((e) => e.tagName === "MAIN" || (/auto|scroll/.test(getComputedStyle(e).overflowY) && getComputedStyle(e).maxHeight === "none"))
                .map((e) => e.scrollHeight - e.clientHeight),
            ),
          );
          if (extra <= 0) break;
          await page.setViewportSize({ width, height: (h += extra) });
          await page.waitForTimeout(200);
        }
        await page.screenshot({ path: path.join(out, `${id}.png`) });
        await page.close();
      }
      await context.close();
    }
  }
  await browser.close();
  if (failures.length > 0) {
    console.error(failures.join("\n"));
    process.exit(1);
  }
  console.log(`screens: ${Object.keys(views).length * 4} screenshots in docs/screens`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
