// @vitest-environment jsdom
import { act } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";
import type { OpsHandlers } from "../realtime";
import { AppShell } from "./AppShell";

const m = vi.hoisted(() => ({
  pending: [] as ((attention: number) => void)[],
  ops: {} as { current: Record<string, () => void> },
}));
vi.mock("../api", () => ({
  api: () => new Promise((resolve) => m.pending.push((attention) => resolve({ counts: { attention } }))),
}));
vi.mock("../realtime", () => ({
  useOps: (h: OpsHandlers) => {
    m.ops.current = h as Record<string, () => void>;
  },
}));

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

describe("AppShell", () => {
  it("shows the count from the latest request and refreshes after a reconnect", async () => {
    const root = document.createElement("div");
    await act(async () =>
      createRoot(root).render(
        <MemoryRouter>
          <AppShell dark={false} onToggleTheme={() => undefined} />
        </MemoryRouter>,
      ),
    );
    const badge = () => root.querySelector("[aria-label^='확인 필요']")?.getAttribute("aria-label");

    act(() => m.ops.current.jobUpdated());
    await act(async () => m.pending[1](2));
    await act(async () => m.pending[0](5));
    expect(badge()).toBe("확인 필요 2건");

    act(() => m.ops.current.connected());
    await act(async () => m.pending[2](3));
    expect(badge()).toBe("확인 필요 3건");
  });
});
