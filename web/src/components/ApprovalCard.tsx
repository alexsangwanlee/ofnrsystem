import { useState } from "react";
import { useNavigate } from "react-router";
import { Badge, Body1, Button, Field, Text, Textarea, makeStyles, tokens } from "@fluentui/react-components";
import { api } from "../api";
import type { AttentionDto, Decision } from "../types";
import { useAction, useUi } from "./ui";

const KIND_LABEL: Record<AttentionDto["kind"], string> = {
  approval: "확인 요청",
  review: "결과물 검토",
  failed: "실패",
  blocked: "앱 준비 필요",
};
const OPEN_LABEL: Record<AttentionDto["kind"], string> = {
  approval: "작업 보기",
  review: "결과 보기",
  failed: "다시 보기",
  blocked: "작업 보기",
};
const ANSWERED = "답변을 보냈습니다. 같은 작업이 이어서 실행됩니다.";
const CLOSED = "질문을 닫았습니다.";

const useStyles = makeStyles({
  top: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, minWidth: 0 },
  detail: { whiteSpace: "pre-wrap", overflowWrap: "anywhere", color: tokens.colorNeutralForeground2 },
  title: { minWidth: 0, overflowWrap: "anywhere" },
});

/** ask_ceo detail: JSON `{ question, options }`, or plain question text. */
function parseQuestion(detail: string | null): { question: string; options: string[] } {
  try {
    const j = JSON.parse(detail ?? "");
    if (typeof j?.question === "string")
      return { question: j.question, options: Array.isArray(j.options) ? [...new Set<string>(j.options.filter((o: unknown) => typeof o === "string"))] : [] };
  } catch {
    // Not JSON: the detail is the question itself.
  }
  return { question: detail ?? "", options: [] };
}

/**
 * One item that needs the CEO: an AI question to answer, or a job to review, retry or unblock.
 * `onDone` gets the Korean confirmation for a question, since the parent's reload removes this card.
 */
export function ApprovalCard({ item, onDone }: { item: AttentionDto; onDone?: (message?: string) => void }) {
  const s = useStyles();
  const ui = useUi();
  const navigate = useNavigate();
  const { busy, error, run } = useAction();
  const [mode, setMode] = useState<"edit" | "respond">();
  const [text, setText] = useState("");
  const [missing, setMissing] = useState(false);
  const approval = item.kind === "approval" && item.approvalId;
  const asked = approval && item.approvalKind === "question" ? parseQuestion(item.detail) : undefined;
  // A question that fits the title is shown once, wrapped; a longer one keeps a one-line title over the full text.
  const question = asked && (asked.question || item.title);
  const detail = asked ? (question !== item.title ? question : undefined) : item.detail;

  const decide = async (decision: Decision, answer?: string) => {
    const body = answer === undefined ? { decision } : { decision, text: answer };
    const ok = await run(() =>
      api(`/api/approvals/${item.approvalId}/decide`, { method: "POST", body: JSON.stringify(body) }),
    );
    if (ok) onDone?.(asked ? (decision === "ignore" ? CLOSED : ANSWERED) : undefined);
  };
  const send = () => {
    if (busy) return;
    if (!text.trim()) return setMissing(true);
    void decide(asked ? "respond" : mode!, text.trim());
  };

  return (
    <article className={ui.card} aria-label={item.title}>
      <div className={s.top}>
        <Badge appearance="tint" shape="rounded" color={item.kind === "failed" ? "danger" : "warning"}>
          {asked ? "AI 질문" : KIND_LABEL[item.kind]}
        </Badge>
        <Text weight="semibold" className={detail ? ui.ellipsis : s.title}>
          {item.title}
        </Text>
      </div>
      {detail && <Body1 className={s.detail}>{detail}</Body1>}
      {asked && asked.options.length > 0 && (
        <div className={ui.actions}>
          {asked.options.map((o) => (
            <Button key={o} disabled={busy} onClick={() => void decide("accept", o)}>
              {o}
            </Button>
          ))}
        </div>
      )}
      {(asked || mode) && (
        <Field
          label={asked ? "답변" : mode === "edit" ? "수정 내용" : "답변 내용"}
          hint={asked ? "Ctrl+Enter로 보냅니다." : undefined}
          validationMessage={missing ? "내용을 입력해 주세요." : undefined}
        >
          <Textarea
            value={text}
            resize="vertical"
            onChange={(_, d) => {
              setText(d.value);
              setMissing(false);
            }}
            onKeyDown={(e) => {
              // Enter that commits a Korean IME syllable must not send; Enter alone is a new line.
              if (!asked || e.key !== "Enter" || !e.ctrlKey || e.nativeEvent.isComposing || e.keyCode === 229) return;
              e.preventDefault();
              send();
            }}
          />
        </Field>
      )}
      {error && <Text className={ui.error}>{error}</Text>}
      <div className={ui.actions}>
        {asked && (
          <>
            <Button appearance="primary" disabled={busy} onClick={send}>
              답변 보내기
            </Button>
            <Button appearance="subtle" disabled={busy} onClick={() => void decide("ignore")}>
              무시
            </Button>
          </>
        )}
        {approval && !asked && mode && (
          <>
            <Button appearance="primary" disabled={busy} onClick={send}>
              보내기
            </Button>
            <Button onClick={() => setMode(undefined)}>취소</Button>
          </>
        )}
        {approval && !asked && !mode && (
          <>
            <Button appearance="primary" disabled={busy} onClick={() => void decide("accept")}>
              승인
            </Button>
            <Button onClick={() => setMode("edit")}>수정 후 승인</Button>
            <Button onClick={() => setMode("respond")}>답변</Button>
            <Button appearance="subtle" disabled={busy} onClick={() => void decide("ignore")}>
              무시
            </Button>
          </>
        )}
        {!approval && <Button onClick={() => navigate(`/jobs/${item.jobId}`)}>{OPEN_LABEL[item.kind]}</Button>}
      </div>
    </article>
  );
}
