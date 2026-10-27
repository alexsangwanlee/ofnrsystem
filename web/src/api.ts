import { invoke, isTauri } from "@tauri-apps/api/core";
import type { ApiErrorBody, Engine, EngineDto } from "./types";

export interface ServerInfo { baseUrl: string; token: string }

export class ApiError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

const TOKEN_KEY = "ofnr.token";
let serverInfo: Promise<ServerInfo> | undefined;

function readBrowserToken(): string {
  const url = new URL(window.location.href);
  const fromUrl = url.searchParams.get("token");
  if (fromUrl) {
    sessionStorage.setItem(TOKEN_KEY, fromUrl);
    // Keep the token out of the address bar, history and screenshots.
    url.searchParams.delete("token");
    window.history.replaceState(window.history.state, "", url);
  }
  return sessionStorage.getItem(TOKEN_KEY) ?? "";
}

// Runs at import, before BrowserRouter copies the URL into its state; a later strip would let
// useSearchParams write the token back into the address bar.
const browserToken = isTauri() ? "" : readBrowserToken();

export function getServerInfo(): Promise<ServerInfo> {
  serverInfo ??= (
    isTauri()
      ? invoke<ServerInfo>("server_info")
      : Promise.resolve({ baseUrl: import.meta.env.VITE_API ?? "", token: browserToken })
  ).catch((e: unknown) => {
    // The sidecar may still be starting, so the next call asks again.
    serverInfo = undefined;
    throw e;
  });
  return serverInfo;
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (typeof init.body === "string" && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");

  let res: Response;
  try {
    const { baseUrl, token } = await getServerInfo();
    headers.set("X-Ofnr-Token", token);
    res = await fetch(baseUrl + path, { ...init, headers });
  } catch {
    throw new ApiError(0, "서버에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.");
  }
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as ApiErrorBody | null;
    throw new ApiError(res.status, body?.error ?? `요청을 처리하지 못했습니다. (${res.status})`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** Runs a one-turn job that calls the dashboard MCP, so it catches login and MCP approval problems. */
export const checkEngine = (engine: Engine) => api<EngineDto>(`/api/health/engine-check/${engine}`, { method: "POST" });
