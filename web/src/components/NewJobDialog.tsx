import { useState } from "react";
import { useNavigate } from "react-router";
import {
  Body1,
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Select,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { Dismiss24Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { ENGINE, lockLabel } from "../format";
import type { Engine, JobDto, MailSummaryDto, RecipeDto, SettingsDto, Stage } from "../types";
import { Async, useAction, useApi, useUi } from "./ui";

const useStyles = makeStyles({
  form: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
  mail: {
    padding: tokens.spacingHorizontalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    display: "flex",
    flexDirection: "column",
  },
});

/** Slide-over that starts a job: recipe (the stage's recipes first), engine allowed by the recipe, instruction. */
export function NewJobDialog({
  open,
  onClose,
  projectId,
  stage,
  mail,
  recipeId,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  projectId?: string | null;
  stage?: Stage | null;
  mail?: MailSummaryDto | null;
  recipeId?: string;
  onCreated?: (job: JobDto) => void;
}) {
  const s = useStyles();
  const ui = useUi();
  const navigate = useNavigate();
  const recipes = useApi<RecipeDto[]>(open ? "/api/recipes" : null);
  const settings = useApi<SettingsDto>(open ? "/api/settings" : null);
  const { busy, error, run } = useAction();
  const [picked, setPicked] = useState<string>();
  const [engine, setEngine] = useState<Engine>();
  const [instruction, setInstruction] = useState("");
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    // The dialog stays mounted between chips and projects; every open starts from the caller's recipe.
    setWasOpen(open);
    if (open) {
      setPicked(undefined);
      setEngine(undefined);
      setInstruction("");
    }
  }

  // skill-learning writes the company skills, so it starts only from 검토 의견, never with mail text attached.
  // The stage only orders the list: client mail and Production projects need recipes of other stages too.
  const options = (recipes.data ?? [])
    .filter((r) => r.id !== "skill-learning" && (r.id === recipeId || projectId || !r.needsProject))
    .sort((a, b) => Number(b.stage === stage) - Number(a.stage === stage));
  const recipe = options.find((r) => r.id === (picked ?? recipeId)) ?? options[0];
  const engines = recipe?.engines ?? [];
  const preferred = settings.data?.defaultEngine;
  const chosen = engine && engines.includes(engine) ? engine : preferred && engines.includes(preferred) ? preferred : engines[0];

  const submit = () => {
    if (!recipe) return;
    void run(async () => {
      const job = await api<JobDto>("/api/jobs", {
        method: "POST",
        body: JSON.stringify({
          recipeId: recipe.id,
          projectId: projectId ?? null,
          instruction: instruction.trim(),
          engine: chosen ?? null,
          mailId: mail?.id ?? null,
        }),
      });
      onClose();
      if (onCreated) onCreated(job);
      else navigate(`/jobs/${job.id}`);
    });
  };

  return (
    <OverlayDrawer open={open} position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="닫기" icon={<Dismiss24Regular />} onClick={onClose} />}
        >
          작업 맡기기
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <Async state={recipes}>
          {() =>
            options.length === 0 ? (
              <Body1>지금 맡길 수 있는 작업이 없습니다. 프로젝트를 먼저 연결해 주세요.</Body1>
            ) : (
              <div className={s.form}>
                <Field
                  label="작업 종류"
                  hint={recipe?.lock ? `앱 잠금: ${lockLabel(recipe.lock)}. 같은 앱을 쓰는 작업은 한 번에 하나씩 실행됩니다.` : undefined}
                >
                  {/* Mounted after the recipes load, so it takes focus from the drawer's X button. */}
                  <Select autoFocus value={recipe?.id} onChange={(_, d) => setPicked(d.value)}>
                    {options.map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.name}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="엔진">
                  <RadioGroup layout="horizontal" value={chosen} onChange={(_, d) => setEngine(d.value as Engine)}>
                    {engines.map((e) => (
                      <Radio key={e} value={e} label={ENGINE[e]} />
                    ))}
                  </RadioGroup>
                </Field>
                <Field label="지시 내용">
                  <Textarea
                    rows={8}
                    resize="vertical"
                    value={instruction}
                    onChange={(_, d) => setInstruction(d.value)}
                  />
                </Field>
                {mail && (
                  <div className={s.mail}>
                    <Caption1 className={ui.muted}>연결 메일</Caption1>
                    <Text weight="semibold">{mail.subject}</Text>
                    <Caption1>{mail.fromName ?? mail.fromAddress}</Caption1>
                  </div>
                )}
                {error && <Text className={ui.error}>{error}</Text>}
              </div>
            )
          }
        </Async>
      </DrawerBody>
      <DrawerFooter>
        <Button appearance="primary" disabled={busy} onClick={submit}>
          작업 시작
        </Button>
        <Button onClick={onClose}>취소</Button>
      </DrawerFooter>
    </OverlayDrawer>
  );
}
