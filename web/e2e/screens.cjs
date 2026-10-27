// Every page in light and dark at 1440x900 and 1024x768 against the server scripts/e2e.sh starts with --seed-demo:
// no console errors, no horizontal page scroll, a screenshot each in docs/screens/<scheme>-<width>-<page>.png.
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

(async () => {
  const [projects, mails, today] = await Promise.all([get("/api/projects"), get("/api/mail?take=200"), get("/api/today")]);
  const project = projects.find((p) => p.name === "샘플 성수 팝업스토어");
  const mail = mails.find((m) => m.status === "NeedsReply");
  const review = today.attention.find((a) => a.kind === "review");
  const routes = {
    today: "/",
    mail: "/mail",
    "mail-detail": `/mail/${mail.id}`,
    projects: "/projects",
    "project-detail": `/projects/${project.id}`,
    "job-detail": `/jobs/${review.jobId}`,
    agents: "/agents",
    skills: "/skills",
    settings: "/settings",
  };

  fs.mkdirSync(out, { recursive: true });
  const browser = await chromium.launch();
  const failures = [];
  for (const scheme of ["light", "dark"]) {
    for (const [width, height] of [[1440, 900], [1024, 768]]) {
      const context = await browser.newContext({ viewport: { width, height }, colorScheme: scheme, locale: "ko-KR" });
      for (const [name, route] of Object.entries(routes)) {
        const page = await context.newPage();
        const errors = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(e.message));
        await page.goto(`${base}${route}?token=${token}`, { waitUntil: "networkidle" });
        await page.waitForTimeout(400); // skeletons fade out
        const id = `${scheme}-${width}-${name}`;
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        if (overflow > 0) failures.push(`${id}: page scrolls ${overflow}px horizontally`);
        for (const e of errors.filter((e) => !EXPECTED.some((x) => x.test(e)))) failures.push(`${id}: console error: ${e}`);
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
  console.log(`screens: ${Object.keys(routes).length * 4} screenshots in docs/screens`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
