import { useEffect, useRef } from "react";
import { HubConnection, HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { isTauri } from "@tauri-apps/api/core";
import { getServerInfo } from "./api";
import type { ApprovalDto, JobDto, JobEventDto, MailSummaryDto, NotifyDto, ProjectDto } from "./types";

export interface OpsHandlers {
  jobUpdated?: (job: JobDto) => void;
  jobEvent?: (event: JobEventDto) => void;
  approvalRequested?: (approval: ApprovalDto) => void;
  approvalResolved?: (approval: ApprovalDto) => void;
  mailUpdated?: (mail: MailSummaryDto) => void;
  projectUpdated?: (project: ProjectDto) => void;
  notify?: (n: NotifyDto) => void;
  /** After every (re)connect; events sent while disconnected are lost, so reload what is shown. */
  connected?: () => void;
}

const METHODS = [
  "jobUpdated",
  "jobEvent",
  "approvalRequested",
  "approvalResolved",
  "mailUpdated",
  "projectUpdated",
  "notify",
] as const satisfies readonly (keyof OpsHandlers)[];

const listeners = new Set<{ current: OpsHandlers }>();
const joinedJobs = new Set<string>();
let hub: Promise<HubConnection> | undefined;

function connect(): Promise<HubConnection> {
  hub ??= getServerInfo().then(({ baseUrl, token }) => {
    const c = new HubConnectionBuilder()
      // The WebSocket carries the token as ?access_token; negotiate is a plain POST and needs the header.
      .withUrl(`${baseUrl}/hubs/ops`, { accessTokenFactory: () => token, headers: { "X-Ofnr-Token": token } })
      .withAutomaticReconnect()
      .build();
    for (const m of METHODS) {
      c.on(m, (arg: never) => listeners.forEach((l) => (l.current[m] as ((a: never) => void) | undefined)?.(arg)));
    }
    c.on("notify", (n: NotifyDto) => void showDesktopNotification(n));
    c.onreconnected(() => onConnected(c));
    c.onclose(() => void startUntilConnected(c));
    void startUntilConnected(c);
    return c;
  }).catch((e: unknown) => {
    hub = undefined;
    throw e;
  });
  return hub;
}

async function startUntilConnected(c: HubConnection) {
  while (c.state === HubConnectionState.Disconnected) {
    try {
      await c.start();
      onConnected(c);
    } catch {
      await new Promise((r) => setTimeout(r, 5000));
    }
  }
}

function onConnected(c: HubConnection) {
  // Groups do not survive a reconnect (new connection id), so rejoin open job pages.
  joinedJobs.forEach((id) => void c.invoke("JoinJob", id).catch(() => undefined));
  listeners.forEach((l) => l.current.connected?.());
}

async function showDesktopNotification(n: NotifyDto) {
  if (isTauri()) {
    const { isPermissionGranted, requestPermission, sendNotification } = await import("@tauri-apps/plugin-notification");
    if ((await isPermissionGranted()) || (await requestPermission()) === "granted") {
      sendNotification({ title: n.title, body: n.body });
    }
  } else if ("Notification" in window && Notification.permission === "granted") {
    new Notification(n.title, { body: n.body });
  }
}

/** Subscribes to hub events for the lifetime of the calling component. Handlers may change every render. */
export function useOps(handlers: OpsHandlers) {
  const ref = useRef(handlers);
  useEffect(() => {
    ref.current = handlers;
  });
  useEffect(() => {
    const entry = ref;
    listeners.add(entry);
    // A failed connect is retried by the next useOps mount or joinJob.
    connect().catch(() => undefined);
    return () => {
      listeners.delete(entry);
    };
  }, []);
}

export async function joinJob(id: string) {
  joinedJobs.add(id);
  const c = await connect();
  if (c.state === HubConnectionState.Connected) await c.invoke("JoinJob", id);
}

export async function leaveJob(id: string) {
  joinedJobs.delete(id);
  const c = await connect();
  if (c.state === HubConnectionState.Connected) await c.invoke("LeaveJob", id);
}
