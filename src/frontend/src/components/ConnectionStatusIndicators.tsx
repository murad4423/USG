import type { BridgeStatus, MachineStatus } from "../useConnectionStatus";
import { Spinner } from "./StateMessage";
import "../styles/connection-status.css";

const BRIDGE_TEXT: Record<BridgeStatus, string> = {
  checking: "Checking bridge...",
  ok: "Bridge OK",
  unavailable: "Bridge unavailable"
};

function machineText(machine: MachineStatus): string {
  const who = machine.name ? ` (${machine.name})` : "";
  switch (machine.state) {
    case "checking":
      return "Checking machine...";
    case "not-configured":
      return "Machine not set up";
    case "connected":
      return `Machine connected${who}`;
    case "unreachable":
      return `Machine not reachable${who}`;
    case "unknown":
      return "Machine status unknown";
  }
}

function machineTooltip(machine: MachineStatus): string {
  switch (machine.state) {
    case "connected":
      return `${machineText(machine)} — updated live${machine.elapsedMs != null ? ` (${machine.elapsedMs} ms)` : ""}.`;
    case "not-configured":
      return "Enter the machine's AE Title, IP address and port in Settings > DICOM Configuration.";
    case "unreachable":
    case "unknown":
      return machine.detail ? `${machineText(machine)}. ${machine.detail}` : machineText(machine);
    default:
      return machineText(machine);
  }
}

interface ConnectionStatusIndicatorsProps {
  /** "rail": the two small dots at the bottom of the slim strip. "nav": the text lines at the bottom of the menu. */
  variant: "rail" | "nav";
  bridge: BridgeStatus;
  machine: MachineStatus;
}

/**
 * The live connection indicators: the native bridge and the ultrasound machine.
 * The colours follow the existing bridge dot: green = fine, red = a problem, grey = not known / not set up.
 */
export default function ConnectionStatusIndicators({ variant, bridge, machine }: ConnectionStatusIndicatorsProps) {
  if (variant === "rail") {
    const machineClass =
      machine.state === "connected" ? " app-rail__status--ok" : machine.state === "unreachable" ? " app-rail__status--unavailable" : "";

    return (
      <div className="app-rail__statuses">
        <span
          className={`app-rail__status app-rail__status--${bridge}`}
          title={BRIDGE_TEXT[bridge]}
          aria-label={BRIDGE_TEXT[bridge]}
        />
        <span
          className={`app-rail__status${machineClass}`}
          title={machineTooltip(machine)}
          aria-label={machineText(machine)}
        />
      </div>
    );
  }

  return (
    <div className="connection-status">
      <div className={`bridge-status bridge-status--${bridge}`}>
        {bridge === "checking" && <Spinner label={BRIDGE_TEXT.checking} />}
        {bridge === "ok" && "● Bridge OK"}
        {bridge === "unavailable" && "⚠️ Bridge unavailable"}
      </div>
      <div className={`machine-status machine-status--${machine.state}`} title={machineTooltip(machine)}>
        {machine.state === "checking" && <Spinner label="Checking machine..." />}
        {machine.state === "connected" && `● ${machineText(machine)}`}
        {machine.state === "unreachable" && `⚠️ ${machineText(machine)}`}
        {machine.state === "unknown" && `○ ${machineText(machine)}`}
        {machine.state === "not-configured" && `○ ${machineText(machine)}`}
      </div>
    </div>
  );
}
