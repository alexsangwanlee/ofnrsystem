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

const useStyles = makeStyles({
  top: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, minWidth: 0 },
  detail: { whiteSpace: "pre-wrap", overflowWrap: "anywhere", color: tokens.colorNeutralForeground2 },
});

/** One item that needs the CEO: an AI question to answer, or a job to review, retry or unblock. */
export function ApprovalCard({ item, onDone }: { item: AttentionDto; onDone?: () => void }) {
  const s = useStyles();
  const ui = useUi();
  const navigate = useNavigate();
  const { busy, error, run } = useAction();
  const [mode, setMode] = useState<"edit" | "respond">();
  const [text, setText] = useState("");
  const [missing, setMissing] = useState(false);

  const decide = async (decision: Decision, answer?: string) => {
    const body = answer === undefined ? { decision } : { decision, text: answer };
    const ok = await run(() =>
      api(`/api/approvals/${item.approvalId}/decide`, { method: "POST", body: JSON.stringify(body) }),
    );
    if (ok) onDone?.();
  };
  const send = () => {
    if (!text.trim()) return setMissing(true);
    void decide(mode!, text.trim());
  };
  const question = item.kind === "approval" && item.approvalId;

  return (
    <article className={ui.card} aria-label={item.title}>
      <div className={s.top}>
        <Badge appearance="tint" shape="rounded" color={item.kind === "failed" ? "danger" : "warning"}>
          {KIND_LABEL[item.kind]}
        </Badge>
        <Text weight="semibold" className={ui.ellipsis}>
          {item.title}
        </Text>
      </div>
      {item.detail && <Body1 className={s.detail}>{item.detail}</Body1>}
      {mode && (
        <Field
          label={mode === "edit" ? "수정 내용" : "답변 내용"}
          validationMessage={missing ? "내용을 입력해 주세요." : undefined}
        >
          <Textarea
            value={text}
            resize="vertical"
            onChange={(_, d) => {
              setText(d.value);
              setMissing(false);
            }}
          />
        </Field>
      )}
      {error && <Text className={ui.error}>{error}</Text>}
      <div className={ui.actions}>
        {question && mode && (
          <>
            <Button appearance="primary" disabled={busy} onClick={send}>
              보내기
            </Button>
            <Button onClick={() => setMode(undefined)}>취소</Button>
          </>
        )}
        {question && !mode && (
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
        {!question && <Button onClick={() => navigate(`/jobs/${item.jobId}`)}>{OPEN_LABEL[item.kind]}</Button>}
      </div>
    </article>
  );
}
