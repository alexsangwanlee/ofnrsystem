import { useState } from "react";
import {
  Button,
  Combobox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  Option,
  OverlayDrawer,
  Select,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { Dismiss24Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { stageLabel } from "../format";
import { STAGES, type ClientDto, type ProjectDto, type ProjectInput, type Stage } from "../types";
import { useAction, useApi, useUi, useValidation } from "./ui";

const useStyles = makeStyles({
  form: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalM },
  pair: {
    display: "grid",
    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
    gap: tokens.spacingHorizontalM,
    "& .fui-Input": { minWidth: 0 },
  },
});

const DATES = [
  ["installDate", "설치일"],
  ["openDate", "오픈일"],
  ["endDate", "종료일"],
  ["teardownDate", "철수일"],
] as const;
const AMOUNTS = [
  ["quoteAmount", "견적액"],
  ["contractAmount", "계약액"],
  ["costAmount", "실행원가"],
] as const;

export type ProjectSeed = ProjectInput & { clientName?: string | null };

/** ProjectInput from an existing project, for PUT and for prefilling the form. */
export function toInput(p: ProjectDto): ProjectSeed {
  return {
    name: p.name,
    slug: p.slug,
    clientId: p.clientId,
    clientName: p.clientName,
    stage: p.stage,
    venue: p.venue,
    installDate: p.installDate,
    openDate: p.openDate,
    endDate: p.endDate,
    teardownDate: p.teardownDate,
    quoteAmount: p.quoteAmount,
    contractAmount: p.contractAmount,
    costAmount: p.costAmount,
    instructions: p.instructions ?? null,
  };
}

type Values = Record<string, string>;

const KEYS = ["name", "slug", "clientName", "stage", "venue", "instructions", ...DATES.map((d) => d[0]), ...AMOUNTS.map((a) => a[0])];

function toValues(seed: ProjectSeed): Values {
  const v: Values = {};
  for (const k of KEYS) v[k] = String(seed[k as keyof ProjectSeed] ?? "");
  for (const [k] of DATES) v[k] = v[k].slice(0, 10);
  v.stage ||= "Inquiry";
  return v;
}

/**
 * Slide-over for creating or editing a project. A client name that does not exist yet is created
 * on save, so the CEO never has to leave the form to add one.
 */
export function ProjectForm({
  open,
  title,
  seed,
  editing,
  onSubmit,
  onClose,
}: {
  open: boolean;
  title: string;
  seed: ProjectSeed;
  /** Existing project: stage moves go through the board, and the folder name is fixed. */
  editing?: boolean;
  onSubmit: (input: ProjectInput) => Promise<unknown>;
  onClose: () => void;
}) {
  const s = useStyles();
  const ui = useUi();
  const clients = useApi<ClientDto[]>(open ? "/api/clients" : null);
  const { busy, error, run } = useAction();
  const [v, setV] = useState(() => toValues(seed));
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    // Every open starts from the caller's current seed (another mail, another project).
    setWasOpen(open);
    if (open) setV(toValues(seed));
  }

  const errors: Record<string, string | undefined> = {
    name: v.name.trim() ? undefined : "프로젝트명을 입력해 주세요.",
    slug: /^[A-Za-z0-9-]{0,24}$/.test(v.slug.trim()) ? undefined : "영문, 숫자, - 만 24자까지 쓸 수 있습니다.",
  };
  for (const [k] of AMOUNTS)
    errors[k] = /^\d*$/.test(v[k].replace(/,/g, "")) ? undefined : "숫자만 입력해 주세요.";
  const val = useValidation(errors);

  const field = (name: string) => ({
    id: val.id(name),
    value: v[name],
    onBlur: val.blur(name),
    onChange: (_: unknown, d: { value: string }) => {
      val.edit(name);
      setV((x) => ({ ...x, [name]: d.value }));
    },
  });
  const text = (k: string) => v[k].trim() || null;
  const amount = (k: string) => (v[k].trim() ? Number(v[k].replace(/,/g, "")) : null);

  const submit = () => {
    if (!val.check()) return;
    void run(async () => {
      const name = v.clientName.trim();
      let clientId = name ? clients.data?.find((c) => c.name === name)?.id : null;
      if (name && !clientId) {
        const created = await api<ClientDto>("/api/clients", {
          method: "POST",
          body: JSON.stringify({ name, mailDomains: [] }),
        });
        clientId = created.id;
      }
      await onSubmit({
        name: v.name.trim(),
        slug: editing ? seed.slug : text("slug"),
        clientId: clientId ?? null,
        stage: v.stage as Stage,
        venue: text("venue"),
        installDate: text("installDate"),
        openDate: text("openDate"),
        endDate: text("endDate"),
        teardownDate: text("teardownDate"),
        quoteAmount: amount("quoteAmount"),
        contractAmount: amount("contractAmount"),
        costAmount: amount("costAmount"),
        instructions: text("instructions"),
      });
      onClose();
    });
  };

  return (
    <OverlayDrawer open={open} position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="닫기" icon={<Dismiss24Regular />} onClick={onClose} />}
        >
          {title}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <form
          className={s.form}
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <Field label="프로젝트명" required validationMessage={val.message("name")}>
            <Input {...field("name")} />
          </Field>
          {!editing && (
            <Field
              label="영문 약칭 (선택)"
              hint="폴더 이름에 쓰입니다. 비워 두면 프로젝트명에서 만듭니다."
              validationMessage={val.message("slug")}
            >
              <Input {...field("slug")} placeholder="TNF-Seongsu" />
            </Field>
          )}
          <Field label="거래처 (선택)" hint="목록에 없는 이름을 쓰면 새 거래처로 저장합니다.">
            <Combobox
              freeform
              value={v.clientName}
              onChange={(e) => setV((x) => ({ ...x, clientName: e.target.value }))}
              onOptionSelect={(_, d) => setV((x) => ({ ...x, clientName: d.optionText ?? "" }))}
            >
              {(clients.data ?? []).map((c) => (
                <Option key={c.id} value={c.id}>
                  {c.name}
                </Option>
              ))}
            </Combobox>
          </Field>
          {!editing && (
            <Field label="단계">
              <Select value={v.stage} onChange={(_, d) => setV((x) => ({ ...x, stage: d.value }))}>
                {STAGES.map((st) => (
                  <option key={st} value={st}>
                    {stageLabel(st)}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <Field label="장소 (선택)">
            <Input {...field("venue")} />
          </Field>
          <div className={s.pair}>
            {DATES.map(([k, label]) => (
              <Field key={k} label={`${label} (선택)`}>
                <Input type="date" {...field(k)} />
              </Field>
            ))}
          </div>
          <div className={s.pair}>
            {AMOUNTS.map(([k, label]) => (
              <Field key={k} label={`${label} (선택)`} validationMessage={val.message(k)}>
                <Input inputMode="numeric" contentAfter="원" {...field(k)} />
              </Field>
            ))}
          </div>
          <Field label="지침 (선택)" hint="AI가 이 프로젝트의 모든 작업에서 지킬 내용입니다.">
            <Textarea rows={4} resize="vertical" {...field("instructions")} />
          </Field>
          {error && <Text className={ui.error}>{error}</Text>}
          <button type="submit" hidden />
        </form>
      </DrawerBody>
      <DrawerFooter>
        <Button appearance="primary" disabled={busy} onClick={submit}>
          저장
        </Button>
        <Button onClick={onClose}>취소</Button>
      </DrawerFooter>
    </OverlayDrawer>
  );
}
