/**
 * IPC helpers for Settings > DICOM Configuration: the ultrasound machine's
 * connection details, "Test Connection", and this PC's network info.
 * Kept in its own file so ipc.ts stays untouched. Same WebView2 bridge and
 * request / "...Result" reply pattern as ipc.ts. The host side lives in
 * UltrasoundApp.Host/WebViewBridge.DicomConfig.cs.
 *
 * This PC's own AE Title / listening port are NOT here — those stay in
 * AppSettings (getSettings/saveSettings in ipc.ts).
 */

/** The ultrasound machine's DICOM connection details. */
export interface DicomMachineConfig {
  /** Optional label, e.g. "Samsung HS40 – Room 2". Not used for the connection. */
  machineName: string;
  aeTitle: string;
  ipAddress: string;
  port: number;
}

export const DEFAULT_DICOM_MACHINE_CONFIG: DicomMachineConfig = {
  machineName: "",
  aeTitle: "",
  ipAddress: "",
  port: 104
};

export interface DicomNetworkAddress {
  /** Network adapter name, e.g. "Ethernet". */
  name: string;
  address: string;
  /** True for the adapter with a default gateway (the normal LAN connection). */
  isPrimary: boolean;
}

/** What "Test Connection" sends — the values currently on screen (they don't have to be saved). */
export interface DicomTestRequest {
  /** This PC's AE Title (the caller). */
  callingAeTitle: string;
  machineAeTitle: string;
  ipAddress: string;
  port: number;
}

/** Reply shape shared by all four requests; only the fields relevant to the request are set. */
export interface DicomConfigResult {
  /** The request itself was handled. (A failed connection test is still success: true — see `connected`.) */
  success: boolean;
  error?: string | null;

  // getDicomMachineConfig / saveDicomMachineConfig
  config?: DicomMachineConfig | null;

  // testDicomConnection
  connected?: boolean | null;
  /** How far the test got: "input" | "network" | "association" | "echo" | "done". */
  stage?: string | null;
  message?: string | null;
  hint?: string | null;
  elapsedMs?: number | null;
  tcpReachable?: boolean | null;

  // getDicomNetworkInfo
  hostName?: string | null;
  addresses?: DicomNetworkAddress[] | null;
  listenerRunning?: boolean | null;
  listenerAeTitle?: string | null;
  listenerPort?: number | null;
  listenerError?: string | null;
}

interface DicomConfigReply extends DicomConfigResult {
  type: string;
}

function requestDicomConfig(
  requestType: string,
  unavailableMessage: string,
  payload?: Record<string, unknown>
): Promise<DicomConfigResult> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.resolve({ success: false, error: unavailableMessage });
  }

  return new Promise<DicomConfigResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: DicomConfigReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === `${requestType}Result`) {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: requestType, ...payload }));
  });
}

const BRIDGE_UNAVAILABLE = "The native bridge isn't available. Run this inside the desktop app.";

/** The saved machine connection details (blank defaults if none were saved yet). */
export function getDicomMachineConfig(): Promise<DicomConfigResult> {
  return requestDicomConfig("getDicomMachineConfig", BRIDGE_UNAVAILABLE);
}

/** Saves the machine's details. Leaving both AE Title and IP blank clears them; otherwise all three must be valid. */
export function saveDicomMachineConfig(config: DicomMachineConfig): Promise<DicomConfigResult> {
  return requestDicomConfig("saveDicomMachineConfig", BRIDGE_UNAVAILABLE, { config });
}

/** Tries a DICOM C-ECHO against the machine using the given (possibly unsaved) values. */
export function testDicomConnection(test: DicomTestRequest): Promise<DicomConfigResult> {
  return requestDicomConfig("testDicomConnection", BRIDGE_UNAVAILABLE, { test });
}

/** This PC's IPv4 addresses plus whether the receiver (listener) is currently running. */
export function getDicomNetworkInfo(): Promise<DicomConfigResult> {
  return requestDicomConfig("getDicomNetworkInfo", BRIDGE_UNAVAILABLE);
}
