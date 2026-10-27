// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";

describe("api", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("sends the token from the first page load and removes it from the address bar", async () => {
    window.history.replaceState(null, "", "/mail?token=t0k&x=1");
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ ok: true }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await expect(api("/api/health")).resolves.toEqual({ ok: true });
    await api("/api/today");

    const [url, init] = fetchMock.mock.calls[1] as unknown as [string, RequestInit];
    expect(url).toBe("/api/today");
    expect(new Headers(init.headers).get("X-Ofnr-Token")).toBe("t0k");
    expect(window.location.search).toBe("?x=1");
  });

  it("throws ApiError with the server's Korean message", async () => {
    vi.stubGlobal("fetch", async () => new Response(JSON.stringify({ error: "프로젝트를 찾을 수 없습니다." }), { status: 404 }));
    const err = await api("/api/projects/x").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 404, message: "프로젝트를 찾을 수 없습니다." });
  });
});
