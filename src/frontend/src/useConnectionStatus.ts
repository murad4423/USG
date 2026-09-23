import { useCallback, useEffect, useRef, useState } from "react";
import {
  checkMachineStatus,
  DICOM_MACHINE_CONFIG_CHANGED,
  pingBridge,
  type MachineStatusResult
} from "./ipcConnectionStatus";
import { subscribeHostEvent, type HostStatusMessage } from "./ipcLive";

export type BridgeStatus = "checking" | "ok" | "unavailable";

export interface MachineStatus {
  /**
   * checking: first check still running · not-configured: no machine IP saved yet ·
   * connected / unreachable: result of the latest check · unknown: the host isn't answering, so it couldn't be checked.
   */
  state: "checking" | "not-configured" | "connected" | "unreachable" | "unknown";
  /** The machine's saved name (or AE Title) for display. */
  name?: string;
  /** Why it's unreachable / what to check — shown as a tooltip. */
  detail?: string;
  /** Round-trip time of the latest successful check. */
  elapsedMs?: number;
}

/** No message from the host for this long = the bridge is considered down. (The host sends one every ~4 s.) */
const HEARTBEAT_STALE_MS = 12000;
/** How often the "has the host gone quiet?" watchdog looks. */
const WATCHDOG_MS = 2000;
/** Safety net: an occasional request/response check, in case a push was ever missed. */
const SAFETY_CHECK_MS = 30000;

function machineFromResult(result: MachineStatusResult): MachineStatus {
  if (!result.success) {
    return { state: "unknown", detail: result.error };
  }
  if (!result.configured) {
    return { state: "not-configured" };
  }

  const name = result.config?.machineName?.trim() || result.config?.aeTitle || undefined;
  if (result.reachable) {
    return { state: "connected", name, elapsedMs: result.elapsedMs ?? undefined };
  }

  const detail = [result.message, result.hint].filter(Boolean).join(" ");
  return { state: "unreachable", name, detail: detail || undefined };
}

/** Keeps the previous object when nothing visible changed, so a push every few seconds doesn't re-render the app. */
function sameMachine(a: MachineStatus, b: MachineStatus): boolean {
  return a.state === b.state && a.name === b.name && a.detail === b.detail;
}

/**
 * Live status for the navigation bar.
 *
 *  - The native host PUSHES a "hostStatus" message every few seconds. Receiving it proves the bridge works
 *    (that is the heartbeat) and it carries whether the ultrasound machine saved in Settings is reachable.
 *  - If no push arrives for HEARTBEAT_STALE_MS the bridge is shown as unavailable.
 *  - A ping + machine check is also done once at start-up, right after the machine details are saved in
 *    Settings, and every SAFETY_CHECK_MS, so the indicators never sit on "checking".
 */
export function useConnectionStatus(): { bridgeStatus: BridgeStatus; machine: MachineStatus } {
  const [bridgeStatus, setBridgeStatus] = useState<BridgeStatus>("checking");
  const [machine, setMachine] = useState<MachineStatus>({ state: "checking" });

  const lastAliveRef = useRef<number>(Date.now());
  const lastPushRef = useRef<number>(0);

  const markAlive = useCallback(() => {
    lastAliveRef.current = Date.now();
    setBridgeStatus("ok");
  }, []);

  const applyMachine = useCallback((next: MachineStatus) => {
    setMachine((previous) => (sameMachine(previous, next) ? previous : next));
  }, []);

  useEffect(() => {
    let cancelled = false;
    lastAliveRef.current = Date.now();

    if (!window.chrome?.webview) {
      setBridgeStatus("unavailable");
      setMachine({ state: "unknown", detail: "The native bridge isn't available." });
    }

    // 1) Pushes from the host (heartbeat + machine status).
    const unsubscribe = subscribeHostEvent<HostStatusMessage>("hostStatus", (message) => {
      lastPushRef.current = Date.now();
      markAlive();
      if (message.machine) {
        applyMachine(machineFromResult({ success: true, ...message.machine }));
      }
    });

    // 2) Request/response check (start-up, after Settings save, safety net).
    async function requestChecks() {
      const startedAt = Date.now();

      if (await pingBridge(3000)) {
        if (!cancelled) {
          markAlive();
        }
      }

      const result = await checkMachineStatus();
      if (cancelled) {
        return;
      }
      if (result.success) {
        markAlive();
      }
      // A fresher push beats this (possibly slower) answer.
      if (lastPushRef.current < startedAt) {
        applyMachine(machineFromResult(result));
      }
    }

    void requestChecks();
    const safetyTimer = window.setInterval(() => {
      if (document.visibilityState === "visible") {
        void requestChecks();
      }
    }, SAFETY_CHECK_MS);

    function handleConfigChanged() {
      void requestChecks();
    }
    window.addEventListener(DICOM_MACHINE_CONFIG_CHANGED, handleConfigChanged);

    // 3) Watchdog: the host went quiet.
    const watchdog = window.setInterval(() => {
      if (Date.now() - lastAliveRef.current > HEARTBEAT_STALE_MS) {
        setBridgeStatus("unavailable");
        setMachine((previous) =>
          previous.state === "unknown"
            ? previous
            : { state: "unknown", detail: "The app's background service stopped answering." }
        );
      }
    }, WATCHDOG_MS);

    return () => {
      cancelled = true;
      unsubscribe();
      window.clearInterval(safetyTimer);
      window.clearInterval(watchdog);
      window.removeEventListener(DICOM_MACHINE_CONFIG_CHANGED, handleConfigChanged);
    };
  }, [markAlive, applyMachine]);

  return { bridgeStatus, machine };
}
