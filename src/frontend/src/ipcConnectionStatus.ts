/**
 * IPC helpers for the live connection indicators in the navigation bar:
 *  - pingBridge: is the native host (the C# side) still answering?
 *  - checkMachineStatus: is the ultrasound machine (saved under Settings >
 *    DICOM Configuration) reachable right now?
 * Both have a timeout, so a host that stops answering shows up as a
 * failure instead of leaving the indicator stuck on "checking" forever.
 * Kept separate from ipc.ts, which stays untouched.
 */

/** Dispatched on `window` by the Settings page after the machine details are saved, so the indicator re-checks at once. */
export const DICOM_MACHINE_CONFIG_CHANGED = "dicom-machine-config-changed";

/** Result of one live machine check. */
export interface MachineStatusResult {
  /** The host answered at all (false = no bridge, or the request timed out). */
  success: boolean;
  error?: string;
  /** Machine IP + AE Title are saved (without them there is nothing to check). */
  configured?: boolean;
  /** The machine's DICOM port accepted a connection. */
  reachable?: boolean;
  message?: string | null;
  hint?: string | null;
  elapsedMs?: number | null;
  /** The saved details being checked. */
  config?: { machineName: string; aeTitle: string; ipAddress: string; port: number } | null;
}

interface StatusReply extends MachineStatusResult {
  type: string;
}

/** Sends "ping" and resolves true on "pong", false if there's no bridge or no answer within `timeoutMs`. */
export function pingBridge(timeoutMs = 3000): Promise<boolean> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.resolve(false);
  }

  return new Promise<boolean>((resolve) => {
    const finish = (ok: boolean) => {
      window.clearTimeout(timer);
      bridge.removeEventListener("message", handleMessage);
      resolve(ok);
    };

    const handleMessage = (event: MessageEvent<string>) => {
      try {
        // The host's reply is {"Type":"pong"} (capital T) — accept both spellings.
        const parsed = JSON.parse(event.data);
        if ((parsed.type ?? parsed.Type) === "pong") {
          finish(true);
        }
      } catch {
        // not one of ours
      }
    };

    const timer = window.setTimeout(() => finish(false), timeoutMs);
    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "ping" }));
  });
}

/** Asks the host to check the saved machine right now (a quick TCP connect to its DICOM port). */
export function checkMachineStatus(timeoutMs = 8000): Promise<MachineStatusResult> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.resolve({ success: false, error: "The native bridge isn't available." });
  }

  return new Promise<MachineStatusResult>((resolve) => {
    const finish = (result: MachineStatusResult) => {
      window.clearTimeout(timer);
      bridge.removeEventListener("message", handleMessage);
      resolve(result);
    };

    const handleMessage = (event: MessageEvent<string>) => {
      let message: StatusReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "checkDicomMachineStatusResult") {
        const { type: _type, ...result } = message;
        finish(result);
      }
    };

    const timer = window.setTimeout(
      () => finish({ success: false, error: "The host did not answer the machine check in time." }),
      timeoutMs
    );
    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "checkDicomMachineStatus" }));
  });
}
