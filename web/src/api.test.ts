// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const tauri = vi.hoisted(() => ({ on: false, invoke: vi.fn() }));
vi.mock("@tauri-apps/api/core", () => ({ isTauri: () => tauri.on, invoke: tauri.invoke }));

// A fresh module instance is what a page load or reload gives you.
const load = () => import("./api");
const ok = () => new Response(JSON.stringify({ ok: true }), { status: 200 });
const sentToken = (fetchMock: ReturnType<typeof vi.fn>, call: number) =>
  new Headers((fetchMock.mock.calls[call] as unknown as [string, RequestInit])[1].headers).get("X-Ofnr-Token");

describe("api", () => {
  beforeEach(() => {
    vi.resetModules();
    sessionStorage.clear();
    tauri.on = false;
    tauri.invoke.mockReset();
  });
  afterEach(() => vi.unstubAllGlobals());

  it("takes the token from the first page load and removes it from the address bar before the app renders", async () => {
    window.history.replaceState(null, "", "/mail?token=t0k&x=1");
    const { api } = await load();
    expect(window.location.search).toBe("?x=1");

    const fetchMock = vi.fn(async () => ok());
    vi.stubGlobal("fetch", fetchMock);
    await expect(api("/api/health")).resolves.toEqual({ ok: true });
    await api("/api/today");

    expect((fetchMock.mock.calls[1] as unknown as [string])[0]).toBe("/api/today");
    expect(sentToken(fetchMock, 1)).toBe("t0k");
  });

  it("keeps sending the token after a reload without it in the URL", async () => {
    window.history.replaceState(null, "", "/?token=t0k");
    await load();
    vi.resetModules();
    window.history.replaceState(null, "", "/mail");
    const { api } = await load();

    const fetchMock = vi.fn(async () => ok());
    vi.stubGlobal("fetch", fetchMock);
    await api("/api/today");
    expect(sentToken(fetchMock, 0)).toBe("t0k");
  });

  it("asks Tauri again after server_info fails and reports the failure in Korean", async () => {
    tauri.on = true;
    tauri.invoke.mockRejectedValueOnce("not ready").mockResolvedValueOnce({ baseUrl: "http://127.0.0.1:5000", token: "tt" });
    const { api, ApiError } = await load();
    const fetchMock = vi.fn(async () => ok());
    vi.stubGlobal("fetch", fetchMock);

    const err = await api("/api/health").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 0, message: "서버에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요." });

    await expect(api("/api/health")).resolves.toEqual({ ok: true });
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe("http://127.0.0.1:5000/api/health");
    expect(sentToken(fetchMock, 0)).toBe("tt");
    expect(tauri.invoke).toHaveBeenCalledTimes(2);
  });

  it("throws ApiError with the server's Korean message", async () => {
    const { api, ApiError } = await load();
    vi.stubGlobal("fetch", async () => new Response(JSON.stringify({ error: "프로젝트를 찾을 수 없습니다." }), { status: 404 }));
    const err = await api("/api/projects/x").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 404, message: "프로젝트를 찾을 수 없습니다." });
  });
});
