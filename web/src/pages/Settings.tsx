import { useState } from "react";
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
import { api } from "../api";
import { Async, ErrorBar, Page, Section, useAction, useApi, useUi, useValidation } from "../components/ui";
import { ENGINE } from "../format";
import type { CheckDto, Engine, HealthDto, MailAccountDto, SettingsDto } from "../types";

const SECTIONS = [
  ["settings-paths", "작업 폴더"],
  ["settings-engine", "AI 엔진"],
  ["settings-mail", "메일"],
  ["settings-health", "상태 점검"],
] as const;

const useStyles = makeStyles({
  layout: { display: "grid", gridTemplateColumns: "160px minmax(0, 640px)", gap: tokens.spacingHorizontalXXL, alignItems: "start" },
  nav: { position: "sticky", top: 0, display: "flex", flexDirection: "column", gap: tokens.spacingVerticalS },
  sections: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXXL },
  form: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
  pair: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) 120px", gap: tokens.spacingHorizontalM },
  ok: { color: tokens.colorStatusSuccessForeground1, flexShrink: 0 },
  bad: { color: tokens.colorStatusDangerForeground1, flexShrink: 0 },
});

const ascii = (v: string) => (/^[\x20-\x7e]*$/.test(v) ? undefined : "영문, 숫자로 된 경로를 입력해 주세요.");

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
    value: String(v.mail[name] ?? ""),
    onChange: (_: unknown, d: { value: string }) =>
      setMail({ [name]: typeof saved.mail[name] === "number" ? Number(d.value) : d.value || (name === "sentFolder" ? null : "") }),
  });

  const put = (next: SettingsDto, message: string) =>
    save.run(async () => {
      const d = await api<SettingsDto>("/api/settings", { method: "PUT", body: JSON.stringify(next) });
      onSaved(d);
      setForm(form && { ...form, autoTriage: d.autoTriage, appendSentCopy: d.appendSentCopy });
      setNote(message);
    });
  const submit = () => {
    if (val.check()) void put(v, "저장했습니다.").then((ok) => ok && setForm(undefined));
  };
  // Switches save at once, without the unsaved field edits around them.
  const toggle = (patch: Partial<SettingsDto>) => void put({ ...saved, ...patch }, "저장했습니다.");

  const footer = (
    <div className={ui.actions}>
      <Button appearance="primary" disabled={save.busy} onClick={submit}>
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
            <Field label="회사 양식 폴더" hint="견적서, 제안서 양식 파일을 두는 곳입니다.">
              <Input {...text("templatesDir")} />
            </Field>
            <Field label="백업 폴더" hint="데이터베이스를 하루 한 번 백업하고 최근 14개를 남깁니다.">
              <Input {...text("backupsDir")} />
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
              <Field label="포트">
                <Input type="number" {...mailText("imapPort")} />
              </Field>
              <Field label="SMTP 서버">
                <Input {...mailText("smtpHost")} />
              </Field>
              <Field label="포트">
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
            <MailPassword />
            <Switch
              label="새 메일을 AI가 자동으로 분류"
              checked={v.autoTriage}
              disabled={save.busy}
              onChange={(_, d) => toggle({ autoTriage: d.checked })}
            />
            <Switch
              label="보낸 메일 사본을 보낸메일함에 저장"
              checked={v.appendSentCopy}
              disabled={save.busy}
              onChange={(_, d) => toggle({ appendSentCopy: d.checked })}
            />
          </div>
        </Section>
      </div>
    </>
  );
}

function MailPassword() {
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
  const testMail = () =>
    void run(async () => setTest(await api<CheckDto>("/api/settings/mail-test", { method: "POST" })));

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
        <Button disabled={busy} onClick={testMail}>
          연결 확인
        </Button>
        {note && <Caption1 aria-live="polite">{note}</Caption1>}
      </div>
      {test && <CheckRow check={{ ...test, name: test.ok ? "메일 서버에 연결했습니다." : "메일 서버에 연결하지 못했습니다." }} />}
      {error && <ErrorBar message={error} />}
    </div>
  );
}

function CheckRow({ check }: { check: CheckDto }) {
  const s = useStyles();
  const ui = useUi();
  return (
    <div className={ui.row}>
      {check.ok ? <CheckmarkCircle20Filled className={s.ok} aria-label="정상" /> : <ErrorCircle20Filled className={s.bad} aria-label="오류" />}
      <div className={ui.grow}>
        <Body1>{check.name}</Body1>
        {check.detail && <Caption1 className={ui.muted}>{check.detail}</Caption1>}
      </div>
    </div>
  );
}

function Health() {
  const ui = useUi();
  const health = useApi<HealthDto>("/api/health");
  return (
    <div id="settings-health">
      <Section title="상태 점검" actions={<Button size="small" onClick={health.reload}>다시 점검</Button>}>
        <Async state={health} rows={5}>
          {(h) => {
            const checks: CheckDto[] = [
              { ...h.db, name: `데이터베이스: ${h.db.name}` },
              { ...h.mail, name: `메일: ${h.mail.name}` },
              ...h.engines.map((e) => ({
                name: `${e.name} ${e.version ?? ""}`.trim(),
                ok: e.ok && e.loggedIn,
                detail: e.detail ?? (e.loggedIn ? e.path : "로그인이 필요합니다."),
              })),
              ...h.mcp.map((c) => ({ ...c, name: `MCP: ${c.name}` })),
              ...h.paths.map((c) => ({ ...c, name: `경로: ${c.name}` })),
            ];
            return (
              <ul className={ui.list}>
                {checks.map((c, i) => (
                  <li key={i}>
                    <CheckRow check={c} />
                  </li>
                ))}
              </ul>
            );
          }}
        </Async>
      </Section>
    </div>
  );
}
