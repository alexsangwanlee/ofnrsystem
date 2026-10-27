// @vitest-environment jsdom
import { act } from "react";
import { createRoot } from "react-dom/client";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { OpsHandlers } from "./realtime";

interface FakeHub {
  state: string;
  reconnected?: () => void;
  invoke: ReturnType<typeof vi.fn>;
}

const fake = vi.hoisted(() => ({ hubs: [] as FakeHub[], getServerInfo: vi.fn() }));
vi.mock("./api", () => ({ getServerInfo: fake.getServerInfo }));
vi.mock("@microsoft/signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@microsoft/signalr")>()),
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    build() {
      const hub: FakeHub & Record<string, unknown> = {
        state: "Disconnected",
        invoke: vi.fn(async () => undefined),
        on() {},
        onclose() {},
        onreconnected(cb: () => void) {
          hub.reconnected = cb;
        },
        async start() {
          hub.state = "Connected";
        },
      };
      fake.hubs.push(hub);
      return hub;
    }
  },
}));

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
const info = { baseUrl: "http://127.0.0.1:5000", token: "tt" };

async function mountOps(handlers: OpsHandlers) {
  const { useOps } = await import("./realtime");
  function Probe() {
    useOps(handlers);
    return null;
  }
  await act(async () => createRoot(document.createElement("div")).render(<Probe />));
}

describe("realtime", () => {
  beforeEach(() => {
    vi.resetModules();
    fake.hubs.length = 0;
    fake.getServerInfo.mockReset();
  });

  it("connects on the next try after server_info fails", async () => {
    fake.getServerInfo.mockRejectedValueOnce("not ready").mockResolvedValue(info);
    await mountOps({});
    expect(fake.hubs).toHaveLength(0);

    const { joinJob } = await import("./realtime");
    await act(() => joinJob("j1"));
    expect(fake.hubs).toHaveLength(1);
    expect(fake.hubs[0].invoke).toHaveBeenCalledWith("JoinJob", "j1");
  });

  it("tells listeners after the first connect and every reconnect", async () => {
    fake.getServerInfo.mockResolvedValue(info);
    const connected = vi.fn();
    await mountOps({ connected });
    expect(connected).toHaveBeenCalledTimes(1);

    const { joinJob } = await import("./realtime");
    await act(() => joinJob("j1"));
    act(() => fake.hubs[0].reconnected?.());
    expect(connected).toHaveBeenCalledTimes(2);
    expect(fake.hubs[0].invoke).toHaveBeenLastCalledWith("JoinJob", "j1");
  });
});
