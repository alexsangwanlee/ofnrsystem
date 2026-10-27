import { Badge } from "@fluentui/react-components";
import { stageLabel } from "../format";
import type { Stage } from "../types";

export function StageBadge({ stage }: { stage: Stage }) {
  return (
    <Badge appearance="tint" shape="rounded" color={stage === "Done" ? "success" : "brand"}>
      {stageLabel(stage)}
    </Badge>
  );
}
