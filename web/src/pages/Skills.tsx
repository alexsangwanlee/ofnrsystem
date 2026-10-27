import { useState } from "react";
import { Body1, Button, Caption1, Field, Subtitle1, Text, Textarea, makeStyles, mergeClasses, tokens } from "@fluentui/react-components";
import { api } from "../api";
import { EmptyState } from "../components/EmptyState";
import { Async, ErrorBar, Page, Section, useAction, useApi, useUi } from "../components/ui";
import { formatDate, formatTime } from "../format";
import type { SkillDto, SkillHistoryDto } from "../types";

const useStyles = makeStyles({
  panes: { flexGrow: 1, minHeight: 0, display: "grid", gridTemplateColumns: "300px minmax(0, 1fr)", gap: tokens.spacingHorizontalL },
  listPane: { overflowY: "auto", minHeight: 0 },
  item: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    width: "100%",
    textAlign: "start",
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    border: "none",
    background: "none",
    color: tokens.colorNeutralForeground1,
    font: "inherit",
    cursor: "pointer",
    ":hover": { backgroundColor: tokens.colorNeutralBackground1Hover },
    ":focus-visible": { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: "-2px" },
  },
  selected: { backgroundColor: tokens.colorNeutralBackground1Selected },
  clamp: { display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden" },
  detail: { overflowY: "auto", minHeight: 0, display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
  editor: { fontFamily: tokens.fontFamilyMonospace, "& textarea": { fontFamily: tokens.fontFamilyMonospace, minHeight: "360px" } },
  rules: { margin: 0, paddingInlineStart: tokens.spacingHorizontalXL, display: "flex", flexDirection: "column", gap: tokens.spacingVerticalXS },
  placeholder: { display: "flex", alignItems: "center", justifyContent: "center", color: tokens.colorNeutralForeground3 },
});

export function Skills() {
  const s = useStyles();
  const ui = useUi();
  const list = useApi<SkillDto[]>("/api/skills");
  const [name, setName] = useState<string>();

  return (
    <Page fill title="스킬" description="AI가 작업할 때 따르는 회사 규칙입니다. 검토 의견에서 배운 규칙도 여기에 쌓입니다.">
      <div className={s.panes}>
        <div className={s.listPane}>
          <Async state={list} rows={6}>
            {(skills) =>
              skills.length === 0 ? (
                <div className={ui.card}>
                  <EmptyState
                    title="스킬이 없습니다"
                    body="회사 스킬 폴더가 비어 있습니다. 설정의 상태 점검에서 플러그인 폴더를 확인해 주세요."
                    action={<Button onClick={list.reload}>다시 불러오기</Button>}
                  />
                </div>
              ) : (
                <ul className={ui.list} aria-label="스킬 목록">
                  {skills.map((k) => (
                    <li key={k.name}>
                      <button
                        type="button"
                        aria-current={k.name === name || undefined}
                        className={mergeClasses(s.item, k.name === name && s.selected)}
                        onClick={() => setName(k.name)}
                      >
                        <Text weight="semibold">{k.name}</Text>
                        <Caption1 className={mergeClasses(ui.muted, s.clamp)}>{k.description}</Caption1>
                        <Caption1>학습 규칙 {k.learnedRules.length}개</Caption1>
                      </button>
                    </li>
                  ))}
                </ul>
              )
            }
          </Async>
        </div>
        {name ? (
          <SkillEditor key={name} name={name} onSaved={list.reload} />
        ) : (
          <div className={s.placeholder}>왼쪽 목록에서 스킬을 고르세요.</div>
        )}
      </div>
    </Page>
  );
}

function SkillEditor({ name, onSaved }: { name: string; onSaved: () => void }) {
  const s = useStyles();
  const ui = useUi();
  const skill = useApi<SkillDto>(`/api/skills/${name}`);
  const history = useApi<SkillHistoryDto[]>(`/api/skills/${name}/history`);
  const [draft, setDraft] = useState<string>();
  const [note, setNote] = useState<string>();
  const { busy, error, run } = useAction();

  const after = (message: string) => {
    setDraft(undefined);
    setNote(message);
    skill.reload();
    history.reload();
    onSaved();
  };
  const save = (content: string) =>
    void run(async () => {
      await api(`/api/skills/${name}`, { method: "PUT", body: JSON.stringify({ content }) });
      after("저장했습니다.");
    });
  const revert = (commit: string) =>
    void run(async () => {
      await api(`/api/skills/${name}/revert`, { method: "POST", body: JSON.stringify({ commit }) });
      after("이전 버전으로 되돌렸습니다.");
    });

  return (
    <div className={s.detail}>
      <Async state={skill} rows={8}>
        {(k) => (
          <>
            <div>
              <Subtitle1 as="h2" block>
                {k.name}
              </Subtitle1>
              <Body1 block className={ui.muted}>
                {k.description}
              </Body1>
            </div>
            <Section title="학습된 규칙">
              {k.learnedRules.length === 0 ? (
                <Body1 className={ui.muted}>
                  아직 학습된 규칙이 없습니다. 작업 상세에서 검토 의견을 이 스킬에 반영하면 여기에 쌓입니다.
                </Body1>
              ) : (
                <ol className={s.rules}>
                  {k.learnedRules.map((r, i) => (
                    <li key={i}>{r}</li>
                  ))}
                </ol>
              )}
            </Section>
            <Section title="내용">
              <Field label="SKILL.md" hint="맨 위 name, description 줄은 지우지 마세요.">
                <Textarea
                  className={s.editor}
                  resize="vertical"
                  value={draft ?? k.content ?? ""}
                  onChange={(_, d) => {
                    setDraft(d.value);
                    setNote(undefined);
                  }}
                />
              </Field>
              {error && <ErrorBar message={error} />}
              <div className={ui.actions}>
                <Button appearance="primary" disabled={busy} onClick={() => save(draft ?? k.content ?? "")}>
                  저장
                </Button>
                {draft !== undefined && <Button onClick={() => setDraft(undefined)}>변경 취소</Button>}
                {note && <Caption1 aria-live="polite">{note}</Caption1>}
              </div>
            </Section>
          </>
        )}
      </Async>
      <Section title="변경 이력">
        <Async state={history} rows={3}>
          {(items) =>
            items.length === 0 ? (
              <Body1 className={ui.muted}>아직 변경 이력이 없습니다.</Body1>
            ) : (
              <ul className={ui.list}>
                {items.map((h, i) => (
                  <li key={h.commit} className={ui.row}>
                    <div className={ui.grow}>
                      <Body1 className={ui.ellipsis}>{h.message}</Body1>
                      <Caption1 className={ui.muted}>
                        {formatDate(h.date)} {formatTime(h.date)} · {h.commit.slice(0, 7)}
                      </Caption1>
                    </div>
                    {i === 0 ? (
                      <Caption1 className={ui.muted}>현재 버전</Caption1>
                    ) : (
                      <Button size="small" disabled={busy} onClick={() => revert(h.commit)}>
                        되돌리기
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            )
          }
        </Async>
      </Section>
    </div>
  );
}
