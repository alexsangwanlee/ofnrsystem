import { Badge, Body1, Button, Caption1, Text, makeStyles, tokens } from "@fluentui/react-components";
import { FolderOpen16Regular, Open16Regular } from "@fluentui/react-icons";
import { api } from "../api";
import { ARTIFACT_STATUS, formatDate, formatSize } from "../format";
import type { FileDto } from "../types";
import { EmptyState } from "./EmptyState";
import { Async, ErrorBar, useAction, useApi, useUi } from "./ui";

const useStyles = makeStyles({
  groups: { display: "flex", flexDirection: "column", gap: tokens.spacingVerticalL },
  groupHead: { display: "flex", alignItems: "center", gap: tokens.spacingHorizontalS, marginBottom: tokens.spacingVerticalXS },
  name: { flexGrow: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
  meta: { flexShrink: 0, color: tokens.colorNeutralForeground3, fontVariantNumeric: "tabular-nums" },
});

/** Joins with the separator the server's path already uses, so Explorer gets a native path. */
export function joinPath(folder: string, relative: string) {
  const sep = folder.includes("\\") ? "\\" : "/";
  return `${folder}${sep}${relative.replace(/[\\/]/g, sep)}`;
}

export function openPath(path: string) {
  return api("/api/open", { method: "POST", body: JSON.stringify({ path }) });
}

/** Project files grouped by top folder (00_IN, 01_PLAN, ...), with version and status chips. */
export function FileList({ projectId, folderPath }: { projectId: string; folderPath: string }) {
  const s = useStyles();
  const ui = useUi();
  const files = useApi<FileDto[]>(`/api/projects/${projectId}/files`);
  const { error, run } = useAction();
  const open = (path: string) => void run(() => openPath(path));

  return (
    <div className={s.groups}>
      {error && <ErrorBar message={error} />}
      <Async state={files} rows={5}>
        {(list) => {
          if (list.length === 0)
            return (
              <div className={ui.card}>
                <EmptyState
                  title="아직 파일이 없습니다"
                  body="받은 자료와 AI가 만든 결과물이 프로젝트 폴더에 생기면 여기에 표시됩니다."
                  action={<Button onClick={() => open(folderPath)}>폴더 열기</Button>}
                />
              </div>
            );
          const groups = new Map<string, FileDto[]>();
          for (const f of list) {
            const parts = f.relativePath.split(/[\\/]/);
            const top = parts.length > 1 ? parts[0] : "";
            groups.set(top, [...(groups.get(top) ?? []), f]);
          }
          return [...groups.entries()]
            .sort(([a], [b]) => a.localeCompare(b))
            .map(([top, items]) => (
              <section key={top} aria-label={top || "프로젝트 폴더"}>
                <div className={s.groupHead}>
                  <Text weight="semibold">{top || "프로젝트 폴더"}</Text>
                  <Caption1 className={ui.muted}>{items.length}개</Caption1>
                  <span style={{ flexGrow: 1 }} />
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<FolderOpen16Regular />}
                    onClick={() => open(top ? joinPath(folderPath, top) : folderPath)}
                  >
                    폴더 열기
                  </Button>
                </div>
                <ul className={ui.list}>
                  {items.map((f) => {
                    const version = /_v(\d{2})\b/.exec(f.relativePath)?.[1];
                    return (
                      <li key={f.relativePath} className={ui.row}>
                        <Body1 className={s.name} title={f.relativePath}>
                          {top ? f.relativePath.slice(top.length + 1) : f.relativePath}
                        </Body1>
                        {version && (
                          <Badge appearance="outline" shape="rounded">
                            v{version}
                          </Badge>
                        )}
                        {f.status && (
                          <Badge appearance="tint" shape="rounded" color={f.status === "Draft" ? "informative" : "success"}>
                            {ARTIFACT_STATUS[f.status]}
                          </Badge>
                        )}
                        <Caption1 className={s.meta}>
                          {formatSize(f.size)} · {formatDate(f.modifiedAt)}
                        </Caption1>
                        <Button size="small" icon={<Open16Regular />} onClick={() => open(joinPath(folderPath, f.relativePath))}>
                          열기
                        </Button>
                      </li>
                    );
                  })}
                </ul>
              </section>
            ));
        }}
      </Async>
    </div>
  );
}
