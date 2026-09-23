/**
 * Live "push" messages from the native host (C#) to the page.
 *
 * Unlike the request/response helpers in ipc.ts, these are messages the host sends on its own:
 *  - "hostStatus"     : sent by the host every few seconds. It doubles as the bridge heartbeat and carries
 *                       the ultrasound machine's current reachability (see WebViewBridge.LiveEvents.cs).
 *  - "studiesChanged" : sent the moment a study/image has been received from the ultrasound machine,
 *                       so the Patient List can refresh itself without a manual refresh.
 */
import type { MachineStatusResult } from "./ipcConnectionStatus";

/** The machine part of a "hostStatus" push (same shape as a machine-status reply, minus `success`). */
export type HostMachineStatus = Omit<MachineStatusResult, "success">;

export interface HostStatusMessage {
  type: "hostStatus";
  machine?: HostMachineStatus;
}

/**
 * Calls `callback` for every message of `type` the host pushes. Returns an unsubscribe function.
 * Does nothing (and returns a no-op) when there is no bridge, e.g. in a plain browser tab.
 */
export function subscribeHostEvent<T = { type: string }>(type: string, callback: (message: T) => void): () => void {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return () => {};
  }

  const handleMessage = (event: MessageEvent<string>) => {
    let message: { type?: string; Type?: string };
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }

    if ((message.type ?? message.Type) === type) {
      callback(message as unknown as T);
    }
  };

  bridge.addEventListener("message", handleMessage);
  return () => bridge.removeEventListener("message", handleMessage);
}
