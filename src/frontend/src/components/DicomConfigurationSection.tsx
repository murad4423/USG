import { useEffect, useState } from "react";
import {
  getDicomNetworkInfo,
  testDicomConnection,
  type DicomConfigResult,
  type DicomMachineConfig,
  type DicomNetworkAddress
} from "../ipcDicomConfig";
import { Spinner } from "./StateMessage";
import "../styles/dicom-configuration.css";

const MAX_AE_TITLE_LENGTH = 16;

interface DicomConfigurationSectionProps {
  /** This PC's AE Title / listening port — part of AppSettings, saved by the Settings page's Save button. */
  aeTitle: string;
  listeningPort: number;
  onAeTitleChange: (value: string) => void;
  onListeningPortChange: (value: number) => void;
  /** The ultrasound machine's details — also saved by the Settings page's Save button. */
  machine: DicomMachineConfig;
  onMachineChange: (next: DicomMachineConfig) => void;
  /** Bump this to re-read the receiver status / IP addresses (the Settings page does it after every Save). */
  refreshKey: number;
}

interface NetworkInfo {
  addresses: DicomNetworkAddress[];
  listenerRunning: boolean;
  listenerAeTitle: string;
  listenerPort: number;
  listenerError?: string;
}

type TestState = "idle" | "testing" | "done";

/** Mirrors the host-side rules, so the Test button can be disabled with a reason instead of failing after a round trip. */
function findInputProblem(aeTitle: string, machine: DicomMachineConfig): string | null {
  const own = aeTitle.trim();
  const machineAe = machine.aeTitle.trim();
  const ip = machine.ipAddress.trim();

  if (!own) {
    return "Enter this PC's AE Title first.";
  }
  if (own.length > MAX_AE_TITLE_LENGTH) {
    return `This PC's AE Title can be at most ${MAX_AE_TITLE_LENGTH} characters.`;
  }
  if (!machineAe) {
    return "Enter the machine's AE Title.";
  }
  if (machineAe.length > MAX_AE_TITLE_LENGTH) {
    return `The machine's AE Title can be at most ${MAX_AE_TITLE_LENGTH} characters.`;
  }
  if (!ip) {
    return "Enter the machine's IP address.";
  }
  if (!Number.isInteger(machine.port) || machine.port < 1 || machine.port > 65535) {
    return "Enter the machine's port (1–65535).";
  }
  return null;
}

/**
 * Settings > "DICOM Configuration". Two sides of the connection:
 *
 *   - This PC (receiver): AE Title + listening port. The ultrasound machine
 *     pushes its images to these, so the receiver's live status is shown.
 *   - The ultrasound machine: AE Title, IP address, port, with a "Test
 *     Connection" button (DICOM C-ECHO) right beside them.
 *
 * Below both, a "What to enter on the machine" table spells out exactly what
 * has to be typed into the machine's DICOM/network setup so it sends images
 * to this PC — built from the values on screen, so it updates as you type.
 *
 * All values are saved by the Settings page's Save button; Test Connection
 * uses whatever is on screen and works before saving.
 */
export default function DicomConfigurationSection({
  aeTitle,
  listeningPort,
  onAeTitleChange,
  onListeningPortChange,
  machine,
  onMachineChange,
  refreshKey
}: DicomConfigurationSectionProps) {
  const [networkState, setNetworkState] = useState<"loading" | "loaded" | "error">("loading");
  const [networkInfo, setNetworkInfo] = useState<NetworkInfo | null>(null);
  const [networkError, setNetworkError] = useState<string | undefined>();
  const [manualRefresh, setManualRefresh] = useState(0);

  const [testState, setTestState] = useState<TestState>("idle");
  const [testResult, setTestResult] = useState<DicomConfigResult | null>(null);

  useEffect(() => {
    let cancelled = false;

    getDicomNetworkInfo().then((result) => {
      if (cancelled) {
        return;
      }
      if (!result.success) {
        setNetworkError(result.error ?? "Could not read this PC's network information.");
        setNetworkState("error");
        return;
      }
      setNetworkInfo({
        addresses: result.addresses ?? [],
        listenerRunning: result.listenerRunning ?? false,
        listenerAeTitle: result.listenerAeTitle ?? "",
        listenerPort: result.listenerPort ?? 0,
        listenerError: result.listenerError ?? undefined
      });
      setNetworkError(undefined);
      setNetworkState("loaded");
    });

    return () => {
      cancelled = true;
    };
  }, [refreshKey, manualRefresh]);

  function updateMachine<K extends keyof DicomMachineConfig>(field: K, value: DicomMachineConfig[K]) {
    onMachineChange({ ...machine, [field]: value });
    // The last result described the old values — don't leave it standing next to new ones.
    setTestResult(null);
    setTestState("idle");
  }

  const inputProblem = findInputProblem(aeTitle, machine);

  async function handleTestConnection() {
    if (inputProblem) {
      return;
    }
    setTestState("testing");
    setTestResult(null);

    const result = await testDicomConnection({
      callingAeTitle: aeTitle.trim(),
      machineAeTitle: machine.aeTitle.trim(),
      ipAddress: machine.ipAddress.trim(),
      port: machine.port
    });

    setTestResult(result);
    setTestState("done");
  }

  const ownAe = aeTitle.trim();
  const receiverChangePending =
    networkInfo !== null &&
    (networkInfo.listenerAeTitle !== ownAe || networkInfo.listenerPort !== listeningPort);
  const allAddresses = networkInfo?.addresses ?? [];
  const primaryAddresses = allAddresses.filter((address) => address.isPrimary);
  const shownAddresses = primaryAddresses.length > 0 ? primaryAddresses : allAddresses;
  const hiddenAddressCount = allAddresses.length - shownAddresses.length;

  return (
    <section className="settings__section">
      <h2 className="settings__section-title">DICOM Configuration</h2>
      <p className="settings__hint dicom-config__intro">
        The ultrasound machine sends its images to this PC. Set this PC's AE Title and port below, enter the
        machine's details on the right and use Test Connection, then copy the values from “What to enter on the
        machine” into the machine's DICOM settings. Click Save at the bottom of the page to keep everything.
      </p>

      <div className="dicom-config__cards">
        {/* ---------- This PC (receiver) ---------- */}
        <div className="dicom-config__card">
          <h3 className="dicom-config__card-title">This PC — receives the images</h3>

          <div className="dicom-config__fields">
            <label className="settings__field">
              <span className="settings__label">AE Title</span>
              <input
                type="text"
                className="settings__input"
                maxLength={MAX_AE_TITLE_LENGTH}
                value={aeTitle}
                onChange={(event) => onAeTitleChange(event.target.value)}
              />
            </label>

            <label className="settings__field">
              <span className="settings__label">Listening Port</span>
              <input
                type="number"
                className="settings__input"
                min={1}
                max={65535}
                value={listeningPort}
                onChange={(event) => onListeningPortChange(Number(event.target.value))}
              />
            </label>
          </div>

          <div className="dicom-config__status">
            {networkState === "loading" && <Spinner label="Checking receiver status..." />}
            {networkState === "error" && <span className="dicom-config__status--bad">⚠️ {networkError}</span>}
            {networkState === "loaded" && networkInfo && (
              <>
                {networkInfo.listenerRunning ? (
                  <span className="dicom-config__status--good">
                    ● Receiver is running — listening on port {networkInfo.listenerPort} as “{networkInfo.listenerAeTitle}”
                  </span>
                ) : (
                  <span className="dicom-config__status--bad">
                    ● Receiver is NOT running
                    {networkInfo.listenerError ? `: ${networkInfo.listenerError}` : " — check the port is free, then Save."}
                  </span>
                )}
                {receiverChangePending && (
                  <span className="dicom-config__status--pending">
                    The AE Title/port shown above are not applied yet — click Save at the bottom of the page.
                  </span>
                )}
              </>
            )}
          </div>
        </div>

        {/* ---------- The ultrasound machine ---------- */}
        <div className="dicom-config__card">
          <h3 className="dicom-config__card-title">Ultrasound machine</h3>

          <div className="dicom-config__fields">
            <label className="settings__field dicom-config__field--wide">
              <span className="settings__label">Machine Name (optional)</span>
              <input
                type="text"
                className="settings__input"
                placeholder="e.g. Samsung HS40 – Room 2"
                maxLength={60}
                value={machine.machineName}
                onChange={(event) => updateMachine("machineName", event.target.value)}
              />
            </label>

            <label className="settings__field">
              <span className="settings__label">Machine AE Title</span>
              <input
                type="text"
                className="settings__input"
                maxLength={MAX_AE_TITLE_LENGTH}
                value={machine.aeTitle}
                onChange={(event) => updateMachine("aeTitle", event.target.value)}
              />
            </label>

            <label className="settings__field">
              <span className="settings__label">Machine IP Address</span>
              <input
                type="text"
                className="settings__input"
                placeholder="e.g. 192.168.1.50"
                value={machine.ipAddress}
                onChange={(event) => updateMachine("ipAddress", event.target.value)}
              />
            </label>

            <label className="settings__field">
              <span className="settings__label">Machine Port</span>
              <input
                type="number"
                className="settings__input"
                min={1}
                max={65535}
                value={machine.port}
                onChange={(event) => updateMachine("port", Number(event.target.value))}
              />
            </label>
          </div>

          <div className="dicom-config__test-row">
            <button
              type="button"
              className="settings__tool-button"
              onClick={handleTestConnection}
              disabled={testState === "testing" || inputProblem !== null}
              title={inputProblem ?? "Check that this PC can reach the machine"}
            >
              {testState === "testing" ? <Spinner label="Testing..." /> : "Test Connection"}
            </button>
            {inputProblem && testState !== "testing" && (
              <span className="dicom-config__test-note">{inputProblem}</span>
            )}
          </div>

          {testState === "done" && testResult && (
            <div
              className={`dicom-config__test-result ${
                testResult.success && testResult.connected
                  ? "dicom-config__test-result--good"
                  : "dicom-config__test-result--bad"
              }`}
              role="status"
            >
              {!testResult.success ? (
                <p className="dicom-config__test-message">⚠️ {testResult.error ?? "The test could not be run."}</p>
              ) : (
                <>
                  <p className="dicom-config__test-message">
                    {testResult.connected ? "✓" : "⚠️"} {testResult.message}
                    {testResult.connected && testResult.elapsedMs != null && ` (${testResult.elapsedMs} ms)`}
                  </p>
                  {testResult.hint && <p className="dicom-config__test-hint">{testResult.hint}</p>}
                </>
              )}
            </div>
          )}
        </div>
      </div>

      {/* ---------- What to enter on the machine ---------- */}
      <div className="dicom-config__card dicom-config__card--wide">
        <div className="dicom-config__card-header">
          <h3 className="dicom-config__card-title">What to enter on the machine</h3>
          <button
            type="button"
            className="dicom-config__link-button"
            onClick={() => setManualRefresh((count) => count + 1)}
            title="Re-read this PC's IP address"
          >
            Refresh
          </button>
        </div>
        <p className="settings__hint dicom-config__table-intro">
          In the machine's DICOM / network / “send to” settings, add a destination (a “DICOM server” or “storage node”)
          with these values:
        </p>

        <table className="dicom-config__table">
          <tbody>
            <tr>
              <th scope="row">AE Title <span>(Destination / Server AE)</span></th>
              <td>{ownAe ? <strong>{ownAe}</strong> : <em>Enter this PC's AE Title above</em>}</td>
            </tr>
            <tr>
              <th scope="row">IP Address <span>(this PC)</span></th>
              <td>
                {networkState === "loading" && <em>Looking up…</em>}
                {networkState === "error" && <em>Unavailable</em>}
                {networkState === "loaded" && shownAddresses.length === 0 && (
                  <em>No network connection found — connect this PC to the same network as the machine.</em>
                )}
                {shownAddresses.map((address) => (
                  <div key={`${address.name}-${address.address}`}>
                    <strong>{address.address}</strong>
                    <span className="dicom-config__adapter"> ({address.name})</span>
                  </div>
                ))}
                {hiddenAddressCount > 0 && (
                  <div className="dicom-config__adapter">
                    + {hiddenAddressCount} other adapter address{hiddenAddressCount === 1 ? "" : "es"} (virtual / not on the main network) not shown.
                  </div>
                )}
              </td>
            </tr>
            <tr>
              <th scope="row">Port <span>(Destination / Server port)</span></th>
              <td>
                <strong>{listeningPort || "—"}</strong>
              </td>
            </tr>
            <tr>
              <th scope="row">Service / Role</th>
              <td>Storage (C-STORE) — “send / store images to this destination”</td>
            </tr>
            <tr>
              <th scope="row">Image compression</th>
              <td>
                Uncompressed only (Explicit or Implicit VR Little Endian). Turn <strong>JPEG / JPEG 2000 / RLE</strong>{" "}
                compression <strong>off</strong> for this destination — compressed images are not accepted.
              </td>
            </tr>
            <tr>
              <th scope="row">Verification <span>(optional)</span></th>
              <td>
                After saving the destination on the machine, use its “Verify” / “Echo” / “Test” button — it should
                report success. This PC answers verification requests.
              </td>
            </tr>
          </tbody>
        </table>

        <ul className="dicom-config__notes">
          <li>
            This PC accepts images from any calling AE Title, so the machine's own AE Title does not need to be
            registered here — it is only used by Test Connection.
          </li>
          <li>
            Windows Firewall must allow incoming connections on port {listeningPort || "…"} for this program, or the
            machine's images will not arrive.
          </li>
          <li>
            Use the IP address on the same network as the machine (usually starting with 192.168. or 10.). If the PC's
            address is assigned automatically it can change — a fixed (static) IP avoids that.
          </li>
        </ul>
      </div>
    </section>
  );
}
