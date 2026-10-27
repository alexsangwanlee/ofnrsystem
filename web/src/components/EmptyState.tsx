import type { ReactNode } from "react";
import { Body1, Subtitle2, makeStyles, tokens, useId } from "@fluentui/react-components";

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: tokens.spacingVerticalS,
    padding: `${tokens.spacingVerticalXXXL} ${tokens.spacingHorizontalL}`,
    textAlign: "center",
  },
  body: { color: tokens.colorNeutralForeground2, marginBottom: tokens.spacingVerticalS },
});

/** Empty list content: what belongs here, why it matters, and one action to start. */
export function EmptyState({ title, body, action }: { title: string; body: string; action: ReactNode }) {
  const s = useStyles();
  const id = useId("empty");
  return (
    <section className={s.root} aria-labelledby={id}>
      <Subtitle2 as="h3" id={id}>
        {title}
      </Subtitle2>
      <Body1 className={s.body}>{body}</Body1>
      {action}
    </section>
  );
}
