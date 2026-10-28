import { useEffect, useState, type ReactNode } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import type { Update } from "@tauri-apps/plugin-updater";
import {
  Body1,
  Button,
  Caption1,
  Field,
  Input,
  Link,
  Radio,
  RadioGroup,
  Switch,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { CheckmarkCircle20Filled, ErrorCircle20Filled } from "@fluentui/react-icons";
import { api, checkEngine } from "../api";
import { Async, ErrorBar, Page, Section, useAction, useApi, useUi, useValidation } from "../components/ui";
import { ENGINE } from "../format";
import { useOps } from "../realtime";
import type { CheckDto, Engine, HealthDto, JobDto, MailAccountDto, SettingsDto } from "../types";

const SECTIONS = [
  ["settings-paths", "작업 폴더"],
  ["settings-engine", "AI 엔진"],
  ["settings-mail", "메일"],
  ["settings-health", "상태 점검"],
  ...(isTauri() ? ([["settings-app", "앱"]] as const) : []),
] as const;

const useStyles = makeStyles({
  layout: { display: "grid", gridTemplateColumns: "160px minmax(0, 640px)", gap: tokens.spacingHorizontalXXL, alignItems: "start" },
  nav: { position: "sticky", top: 0, display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS },
  sections: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXXL },
  form: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
  // Fluent's Input keeps its natural width (about 300px) unless the grid item and the input may shrink.
  pair: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1fr) 120px",
    gap: tokens.spacingHorizontalM,
    "& > *": { minWidth: 0 },
    "& .fui-Input": { minWidth: 0 },
  },
  ok: { color: tokens.colorStatusSuccessForeground1, flexShrink: 0 },
  bad: { color: tokens.colorStatusDangerForeground1, flexShrink: 0 },
});

const required = (v: string) => (v.trim() ? undefined : "경로를 입력해 주세요.");
const ascii = (v: string) => required(v) ?? (/^[\x20-\x7e]*$/.test(v) ? undefined : "영문, 숫자로 된 경로를 입력해 주세요.");
const port = (n: number) => (Number.isInteger(n) && n >= 1 && n <= 65535 ? undefined : "1에서 65535 사이로 입력해 주세요.");

export function Settings() {
  const s = useStyles();
  const settings = useApi<SettingsDto>("/api/settings");
  return (
    <Page title="설정" description="작업 폴더, AI 엔진, 메일 계정을 설정하고 상태를 점검합니다.">
      <div className={s.layout}>
        <nav className={s.nav} aria-label="설정 항목">
          {SECTIONS.map(([id, label]) => (
            <Link key={id} href={`#${id}`}>
              {label}
            </Link>
          ))}
        </nav>
        <div className={s.sections}>
          <Async state={settings} rows={8}>
            {(saved) => <SettingsForm saved={saved} onSaved={(d) => settings.update(() => d)} />}
          </Async>
          <Health />
          {isTauri() && <AppSection />}
        </div>
      </div>
    </Page>
  );
}

function SettingsForm({ saved, onSaved }: { saved: SettingsDto; onSaved: (d: SettingsDto) => void }) {
  const s = useStyles();
  const ui = useUi();
  const [form, setForm] = useState<SettingsDto>();
  const v = form ?? saved;
  const save = useAction();
  const [note, setNote] = useState<string>();

  const errors = {
    projectsRoot: ascii(v.projectsRoot),
    jobsRoot: ascii(v.jobsRoot),
    templatesDir: required(v.templatesDir),
    backupsDir: required(v.backupsDir),
    pgDumpPath: required(v.pgDumpPath),
    imapPort: port(v.mail.imapPort),
    smtpPort: port(v.mail.smtpPort),
    maxConcurrentJobs: v.maxConcurrentJobs >= 1 && v.maxConcurrentJobs <= 8 ? undefined : "1에서 8 사이로 입력해 주세요.",
    mailSyncMinutes: v.mailSyncMinutes >= 1 ? undefined : "1분 이상으로 입력해 주세요.",
  };
  const val = useValidation(errors);
  const set = (patch: Partial<SettingsDto>) => {
    setForm({ ...v, ...patch });
    setNote(undefined);
  };
  const setMail = (patch: Partial<MailAccountDto>) => set({ mail: { ...v.mail, ...patch } });
  const text = (name: keyof SettingsDto) => ({
    id: val.id(name),
    value: String(v[name] ?? ""),
    onBlur: val.blur(name),
    onChange: (_: unknown, d: { value: string }) => {
      val.edit(name);
      set({ [name]: typeof saved[name] === "number" ? Number(d.value) : d.value });
    },
  });
  const mailText = (name: keyof MailAccountDto) => ({
    id: val.id(name),
    value: String(v.mail[name] ?? ""),
    onBlur: val.blur(name),
    onChange: (_: unknown, d: { value: string }) => {
      val.edit(name);
      setMail({ [name]: typeof saved.mail[name] === "number" ? Number(d.value) : d.value || (name === "sentFolder" ? null : "") });
    },
  });

  const put = (next: SettingsDto, message: string) =>
    save.run(async () => {
      const d = await api<SettingsDto>("/api/settings", { method: "PUT", body: JSON.stringify(next) });
      onSaved(d);
      setForm(form && { ...form, autoTriage: d.autoTriage, appendSentCopy: d.appendSentCopy });
      setNote(message);
    });
  const submit = async () => {
    const ok = val.check() && (await put(v, "저장했습니다."));
    if (ok) setForm(undefined);
    return ok;
  };
  // Switches save at once, without the unsaved field edits around them.
  const toggle = (patch: Partial<SettingsDto>) => void put({ ...saved, ...patch }, "저장했습니다.");

  const footer = (
    <div className={ui.actions}>
      <Button appearance="primary" disabled={save.busy} onClick={() => void submit()}>
        변경 저장
      </Button>
      {note && <Caption1 aria-live="polite">{note}</Caption1>}
    </div>
  );

  return (
    <>
      {save.error && <ErrorBar message={save.error} />}
      <div id="settings-paths">
        <Section title="작업 폴더">
          <div className={s.form}>
            <Body1 className={ui.muted}>데이터 폴더: {v.dataRoot}</Body1>
            <Field label="프로젝트 폴더" hint="프로젝트마다 이 아래에 연도별 폴더가 생깁니다. 영문 경로만 쓸 수 있습니다." validationMessage={val.message("projectsRoot")}>
              <Input {...text("projectsRoot")} />
            </Field>
            <Field label="작업 실행 폴더" hint="AI가 작업마다 쓰는 임시 폴더입니다. 영문 경로만 쓸 수 있습니다." validationMessage={val.message("jobsRoot")}>
              <Input {...text("jobsRoot")} />
            </Field>
            <Field label="회사 양식 폴더" hint="견적서, 제안서 양식 파일을 두는 곳입니다." validationMessage={val.message("templatesDir")}>
              <Input {...text("templatesDir")} />
            </Field>
            <Field label="백업 폴더" hint="데이터베이스와 스킬, 회사 양식, 설정 파일을 하루 한 번 백업하고 최근 14개를 남깁니다. PC가 고장 나도 남도록 OneDrive나 NAS 폴더를 지정해 주세요." validationMessage={val.message("backupsDir")}>
              <Input {...text("backupsDir")} />
            </Field>
            <Field
              label="pg_dump 실행 파일"
              hint="백업에 쓰는 PostgreSQL 프로그램입니다. 백업이 실패하면 PostgreSQL 설치 폴더의 bin\pg_dump.exe 경로를 넣어 주세요."
              validationMessage={val.message("pgDumpPath")}
            >
              <Input {...text("pgDumpPath")} />
            </Field>
            {footer}
          </div>
        </Section>
      </div>
      <div id="settings-engine">
        <Section title="AI 엔진">
          <div className={s.form}>
            <Field label="기본 엔진" hint="작업을 맡길 때 처음 선택되는 엔진입니다. 레시피가 허용하는 엔진만 고를 수 있습니다.">
              <RadioGroup layout="horizontal" value={v.defaultEngine} onChange={(_, d) => set({ defaultEngine: d.value as Engine })}>
                {(["claude", "codex"] as const).map((e) => (
                  <Radio key={e} value={e} label={ENGINE[e]} />
                ))}
              </RadioGroup>
            </Field>
            <Field label="동시 실행 수" hint="한 번에 실행할 AI 작업 수입니다. 같은 앱을 쓰는 작업은 따로 하나씩 실행됩니다." validationMessage={val.message("maxConcurrentJobs")}>
              <Input type="number" min={1} max={8} {...text("maxConcurrentJobs")} />
            </Field>
            <Field label="Claude Code 실행 파일">
              <Input {...text("claudeExe")} />
            </Field>
            <Field label="Codex 실행 파일">
              <Input {...text("codexExe")} />
            </Field>
            {footer}
          </div>
        </Section>
      </div>
      <div id="settings-mail">
        <Section title="메일">
          <div className={s.form}>
            <Field label="메일 주소">
              <Input type="email" autoComplete="email" {...mailText("address")} />
            </Field>
            <div className={s.pair}>
              <Field label="IMAP 서버">
                <Input {...mailText("imapHost")} />
              </Field>
              <Field label="포트" validationMessage={val.message("imapPort")}>
                <Input type="number" {...mailText("imapPort")} />
              </Field>
              <Field label="SMTP 서버">
                <Input {...mailText("smtpHost")} />
              </Field>
              <Field label="포트" validationMessage={val.message("smtpPort")}>
                <Input type="number" {...mailText("smtpPort")} />
              </Field>
            </div>
            <Field label="보낸메일함 이름 (선택)" hint="비워 두면 서버에서 자동으로 찾습니다.">
              <Input {...mailText("sentFolder")} />
            </Field>
            <Field label="동기화 간격 (분)" validationMessage={val.message("mailSyncMinutes")}>
              <Input type="number" min={1} {...text("mailSyncMinutes")} />
            </Field>
            {footer}
            <MailPassword saveFirst={form && submit} />
            <Switch
              label="새 메일을 AI가 자동으로 분류"
              checked={v.autoTriage}
              disabled={save.busy}
              onChange={(_, d) => toggle({ autoTriage: d.checked })}
            />
            <Field hint="답장을 한 번 보낸 뒤 NAVER WORKS 보낸메일함에 없을 때만 켜 주세요. 이미 저장되는데 켜면 두 번 저장됩니다.">
              <Switch
                label="보낸 메일 사본을 보낸메일함에 저장"
                checked={v.appendSentCopy}
                disabled={save.busy}
                onChange={(_, d) => toggle({ appendSentCopy: d.checked })}
              />
            </Field>
          </div>
        </Section>
      </div>
    </>
  );
}

/** `saveFirst` is the form's save while it has unsaved edits: 연결 확인 tests the saved account. */
function MailPassword({ saveFirst }: { saveFirst?: () => Promise<boolean> }) {
  const ui = useUi();
  const [password, setPassword] = useState("");
  const [missing, setMissing] = useState(false);
  const [note, setNote] = useState<string>();
  const [test, setTest] = useState<CheckDto>();
  const { busy, error, run } = useAction();

  const savePassword = () => {
    if (!password) return setMissing(true);
    void run(async () => {
      await api("/api/settings/mail-password", { method: "PUT", body: JSON.stringify({ password }) });
      setPassword("");
      setNote("비밀번호를 저장했습니다.");
    });
  };
  const testMail = async () => {
    if (saveFirst && !(await saveFirst())) return; // the form shows the invalid field or the save error
    void run(async () => setTest(await api<CheckDto>("/api/settings/mail-test", { method: "POST" })));
  };

  return (
    <div className={ui.section}>
      <Field
        label="앱 비밀번호"
        hint="NAVER WORKS 외부 앱 비밀번호입니다. 저장한 비밀번호는 다시 표시하지 않습니다."
        validationMessage={missing ? "비밀번호를 입력해 주세요." : undefined}
      >
        <Input
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(_, d) => {
            setPassword(d.value);
            setMissing(false);
            setNote(undefined);
          }}
        />
      </Field>
      <div className={ui.actions}>
        <Button disabled={busy} onClick={savePassword}>
          비밀번호 저장
        </Button>
        <Button disabled={busy} onClick={() => void testMail()}>
          연결 확인
        </Button>
        {note && <Caption1 aria-live="polite">{note}</Caption1>}
      </div>
      {test && <CheckRow check={{ ...test, name: test.ok ? "메일 서버에 연결했습니다." : "메일 서버에 연결하지 못했습니다." }} />}
      {error && <ErrorBar message={error} />}
    </div>
  );
}

function CheckRow({ check, action }: { check: CheckDto; action?: ReactNode }) {
  const s = useStyles();
  const ui = useUi();
  return (
    <div className={ui.row}>
      {check.ok ? <CheckmarkCircle20Filled className={s.ok} aria-label="정상" /> : <ErrorCircle20Filled className={s.bad} aria-label="오류" />}
      <div className={ui.grow}>
        <Body1>{check.name}</Body1>
        {check.detail && <Caption1 className={ui.muted}>{check.detail}</Caption1>}
      </div>
      {action}
    </div>
  );
}

function Health() {
  const ui = useUi();
  const health = useApi<HealthDto>("/api/health");
  const check = useAction();
  const runCheck = (engine: Engine) =>
    void check.run(async () => {
      const e = await checkEngine(engine);
      health.update((h) => ({ ...h, engines: h.engines.map((x) => (x.name === e.name ? e : x)) }));
    });
  return (
    <div id="settings-health">
      <Section title="상태 점검" actions={<Button size="small" onClick={health.reload}>다시 점검</Button>}>
        <Async state={health} rows={5}>
          {(h) => {
            const checks: { check: CheckDto; engine?: Engine }[] = [
              { check: h.db },
              { check: h.mail },
              ...h.engines.map((e) => ({
                engine: e.name,
                check: {
                  name: `${ENGINE[e.name]} ${e.version ?? ""}`.trim(),
                  ok: e.ok && e.loggedIn,
                  detail: e.detail ?? (e.loggedIn ? e.path : "로그인이 필요합니다."),
                },
              })),
              ...h.mcp.map((c) => ({ check: { ...c, name: `MCP: ${c.name}` } })),
              ...h.paths.map((c) => ({ check: { ...c, name: `경로: ${c.name}` } })),
            ];
            return (
              <>
                {check.error && <ErrorBar message={check.error} />}
                <ul className={ui.list}>
                  {checks.map(({ check: c, engine }, i) => (
                    <li key={i}>
                      <CheckRow
                        check={c}
                        action={
                          engine && (
                            <Button size="small" disabled={check.busy} onClick={() => runCheck(engine)}>
                              {check.busy ? "점검 중" : "엔진 점검"}
                            </Button>
                          )
                        }
                      />
                    </li>
                  ))}
                </ul>
              </>
            );
          }}
        </Async>
      </Section>
    </div>
  );
}

/** Tauri only: updates and autostart. Stopping the server for an update would cut off a running job. */
function AppSection() {
  const ui = useUi();
  const running = useApi<JobDto[]>("/api/jobs?status=Running");
  useOps({ jobUpdated: running.reload, connected: running.reload });
  const [update, setUpdate] = useState<Update | null>(null);
  const [note, setNote] = useState<string>();
  const [autostart, setAutostart] = useState<boolean>();
  const { busy, error, run } = useAction();
  const idle = running.data?.length === 0;

  useEffect(() => {
    invoke<boolean>("autostart_enabled").then(setAutostart, () => undefined);
  }, []);

  // Tauri rejects with plain strings, so each step names its own Korean message.
  const step = <T,>(p: Promise<T>, message: string) =>
    p.catch(() => {
      throw new Error(message);
    });
  const checkUpdate = () =>
    void run(async () => {
      const { check } = await import("@tauri-apps/plugin-updater");
      const u = await step(check(), "업데이트를 확인하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.");
      setUpdate(u);
      setNote(u ? `새 버전 ${u.version}이 있습니다.` : "최신 버전을 쓰고 있습니다.");
    });
  const install = (u: Update) =>
    void run(async () => {
      // A job may start after the check or during the download, and stop_backend would kill it.
      const ensureIdle = async () => {
        if ((await api<JobDto[]>("/api/jobs?status=Running")).length > 0) {
          running.reload();
          throw new Error("실행 중인 작업이 끝난 뒤에 업데이트해 주세요.");
        }
      };
      await ensureIdle();
      setNote("업데이트를 내려받는 중입니다. 잠시 뒤 설치 화면이 열립니다.");
      await step(u.download(), "업데이트를 내려받지 못했습니다. 잠시 후 다시 시도해 주세요.");
      await ensureIdle();
      await step(invoke("stop_backend"), "서버를 멈추지 못했습니다. 앱을 다시 시작한 뒤 업데이트해 주세요.");
      await step(u.install(), "업데이트를 설치하지 못했습니다. 앱을 다시 시작해 주세요.");
    });
  const toggleAutostart = (enabled: boolean) =>
    void run(async () => {
      await step(invoke("set_autostart", { enabled }), "자동 실행 설정을 바꾸지 못했습니다.");
      setAutostart(enabled);
    });

  return (
    <div id="settings-app">
      <Section title="앱">
        <div className={ui.section}>
          <Switch
            label="Windows에 로그인하면 자동으로 실행"
            checked={autostart ?? false}
            disabled={autostart === undefined || busy}
            onChange={(_, d) => toggleAutostart(d.checked)}
          />
          <div className={ui.actions}>
            {update ? (
              <Button appearance="primary" disabled={busy || !idle} onClick={() => install(update)}>
                지금 업데이트
              </Button>
            ) : (
              <Button disabled={busy || !idle} onClick={checkUpdate}>
                업데이트 확인
              </Button>
            )}
            {note && <Caption1 aria-live="polite">{note}</Caption1>}
          </div>
          {!idle && running.data && (
            <Caption1 className={ui.muted}>실행 중인 작업이 끝나면 업데이트할 수 있습니다.</Caption1>
          )}
          {running.error && <ErrorBar message={running.error} onRetry={running.reload} />}
          {error && <ErrorBar message={error} />}
        </div>
      </Section>
    </div>
  );
}
