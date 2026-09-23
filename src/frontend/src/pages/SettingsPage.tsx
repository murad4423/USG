import { useEffect, useState } from "react";
import { getSettings, saveSettings, listPrinters, type AppSettings } from "../ipc";
import StateMessage, { LoadingBlock, Spinner } from "../components/StateMessage";
import ImageTemplateUpload from "../components/ImageTemplateUpload";
import DicomConfigurationSection from "../components/DicomConfigurationSection";
import { DICOM_MACHINE_CONFIG_CHANGED } from "../ipcConnectionStatus";
import {
  DEFAULT_DICOM_MACHINE_CONFIG,
  getDicomMachineConfig,
  saveDicomMachineConfig,
  type DicomMachineConfig
} from "../ipcDicomConfig";

const EMPTY_SETTINGS: AppSettings = {
  aeTitle: "",
  listeningPort: 0,
  reportTemplatesFolderPath: "",
  imageSheetBrandingTemplatePath: "",
  printerName: "",
  logoPath: "",
  hospitalName: "",
  address: "",
  headerText: "",
  footerText: ""
};

type LoadState = "loading" | "loaded" | "error";
type SaveState = "idle" | "saving" | "saved" | "error";

/**
 * Settings screen: AE Title/port, template folder path overrides, printer
 * selection, and hospital branding fields — all backed by SettingsService
 * (see ipc.ts's getSettings/saveSettings/listPrinters). Saving takes
 * effect immediately, without an app restart: the host restarts
 * DicomScpListener if AE Title/port changed, and branding/printer
 * selection are read fresh from Settings on every subsequent print — see
 * WebViewBridge's HandleSaveSettings/CurrentImageSheetBranding/
 * ResolvePrinterName.
 */
interface SettingsPageProps {
  /** Opens the DICOM Data Check tool (DicomInspectorPage), handled by App.tsx's page switch. */
  onOpenDicomInspector: () => void;
  /** Opens the Report Template builder (ReportTemplateBuildPage), handled by App.tsx's page switch. */
  onOpenReportTemplateBuilder: () => void;
}

export default function SettingsPage({ onOpenDicomInspector, onOpenReportTemplateBuilder }: SettingsPageProps) {
  const [loadState, setLoadState] = useState<LoadState>("loading");
  const [loadError, setLoadError] = useState<string | undefined>();
  const [settings, setSettings] = useState<AppSettings>(EMPTY_SETTINGS);
  const [printerNames, setPrinterNames] = useState<string[]>([]);
  const [saveState, setSaveState] = useState<SaveState>("idle");
  const [saveMessage, setSaveMessage] = useState<string | undefined>();
  // The ultrasound machine's connection details (DICOM Configuration section) — stored by the host
  // separately from AppSettings, but saved by the same Save button below.
  const [machine, setMachine] = useState<DicomMachineConfig>(DEFAULT_DICOM_MACHINE_CONFIG);
  // Bumped after every Save so the DICOM Configuration section re-reads the receiver status / IP addresses.
  const [dicomRefreshKey, setDicomRefreshKey] = useState(0);

  useEffect(() => {
    let cancelled = false;

    Promise.all([getSettings(), listPrinters(), getDicomMachineConfig()]).then(([settingsResult, printersResult, machineResult]) => {
      if (cancelled) {
        return;
      }

      if (settingsResult.success && settingsResult.settings) {
        setSettings(settingsResult.settings);
        setLoadState("loaded");
      } else {
        setLoadError(settingsResult.error ?? "Could not load settings.");
        setLoadState("error");
      }

      if (printersResult.success && printersResult.printerNames) {
        setPrinterNames(printersResult.printerNames);
      }

      // Not being able to read the machine details shouldn't block the rest of Settings — start blank.
      if (machineResult.success && machineResult.config) {
        setMachine(machineResult.config);
      }
    });

    return () => {
      cancelled = true;
    };
  }, []);

  function updateField<K extends keyof AppSettings>(field: K, value: AppSettings[K]) {
    setSettings((current) => ({ ...current, [field]: value }));
  }

  async function handleSave() {
    setSaveState("saving");
    setSaveMessage(undefined);

    // Machine details first: they are validated strictly, and a typo there should stop the whole save
    // (nothing changes) rather than leave Settings half-saved.
    const machineResult = await saveDicomMachineConfig(machine);
    if (!machineResult.success) {
      setSaveState("error");
      setSaveMessage(`Nothing was saved — ${machineResult.error ?? "the machine connection details are not valid."}`);
      return;
    }
    if (machineResult.config) {
      setMachine(machineResult.config);
    }
    // Tell the navigation bar's live machine indicator to re-check straight away with the new details.
    window.dispatchEvent(new Event(DICOM_MACHINE_CONFIG_CHANGED));

    const result = await saveSettings(settings);
    setDicomRefreshKey((key) => key + 1);

    if (!result.success) {
      setSaveState("error");
      setSaveMessage(result.error ?? "Could not save settings.");
      return;
    }

    if (result.settings) {
      setSettings(result.settings);
    }

    if (result.listenerRestarted && !result.listenerRunning) {
      setSaveState("error");
      setSaveMessage(
        result.listenerError
          ? `Settings saved, but auto-receive is currently down: ${result.listenerError} ` +
            "Fix the AE Title/port and save again."
          : "Settings saved, but the DICOM listener failed to rebind to the new AE Title/port " +
            "(the port may already be in use). Auto-receive is currently down — fix the port and save again."
      );
      return;
    }

    setSaveState("saved");
    setSaveMessage(
      result.listenerRestarted
        ? "Saved — the DICOM listener has been restarted with the new AE Title/port."
        : "Saved."
    );
  }

  if (loadState === "loading") {
    return (
      <div className="page">
        <h1>Settings</h1>
        <LoadingBlock label="Loading settings..." />
      </div>
    );
  }

  if (loadState === "error") {
    return (
      <div className="page">
        <h1>Settings</h1>
        <StateMessage variant="error" icon="⚠️" title="Couldn't load settings" detail={loadError} />
      </div>
    );
  }

  return (
    <div className="page">
      <h1>Settings</h1>

      <section className="settings__section">
        <h2 className="settings__section-title">Tools</h2>
        <div className="settings__grid">
          <button type="button" className="settings__tool-button" onClick={onOpenDicomInspector}>
            DICOM Data Check
          </button>
          <button type="button" className="settings__tool-button" onClick={onOpenReportTemplateBuilder}>
            Report Template
          </button>
        </div>
        <p className="settings__hint">
          Pick any DICOM file to see every field it carries — including fields that are present
          but blank for that file.
        </p>
      </section>

      <DicomConfigurationSection
        aeTitle={settings.aeTitle}
        listeningPort={settings.listeningPort}
        onAeTitleChange={(value) => updateField("aeTitle", value)}
        onListeningPortChange={(value) => updateField("listeningPort", value)}
        machine={machine}
        onMachineChange={setMachine}
        refreshKey={dicomRefreshKey}
      />

      <section className="settings__section">
        <h2 className="settings__section-title">Template Folders</h2>
        <div className="settings__grid">
          <label className="settings__field settings__field--wide">
            <span className="settings__label">Report Templates Folder</span>
            <input
              type="text"
              className="settings__input"
              placeholder="(use built-in default)"
              value={settings.reportTemplatesFolderPath ?? ""}
              onChange={(event) => updateField("reportTemplatesFolderPath", event.target.value)}
            />
          </label>

          <label className="settings__field settings__field--wide">
            <span className="settings__label">Image Sheet Branding Template File</span>
            <input
              type="text"
              className="settings__input"
              placeholder="(use built-in default)"
              value={settings.imageSheetBrandingTemplatePath ?? ""}
              onChange={(event) => updateField("imageSheetBrandingTemplatePath", event.target.value)}
            />
          </label>
        </div>
      </section>

      <ImageTemplateUpload />

      <section className="settings__section">
        <h2 className="settings__section-title">Printer</h2>
        <div className="settings__grid">
          <label className="settings__field">
            <span className="settings__label">Printer</span>
            <select
              className="settings__input"
              value={settings.printerName ?? ""}
              onChange={(event) => updateField("printerName", event.target.value)}
            >
              <option value="">(System default)</option>
              {printerNames.map((name) => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
            </select>
          </label>
        </div>
        {printerNames.length === 0 && (
          <p className="settings__hint">No installed printers were found — printing will fall back to saving a PDF under data/print-fallback/ until a printer is available.</p>
        )}
      </section>

      <section className="settings__section">
        <h2 className="settings__section-title">Hospital Branding</h2>
        <div className="settings__grid">
          <label className="settings__field">
            <span className="settings__label">Logo Path</span>
            <input
              type="text"
              className="settings__input"
              placeholder="(no logo)"
              value={settings.logoPath ?? ""}
              onChange={(event) => updateField("logoPath", event.target.value)}
            />
          </label>

          <label className="settings__field">
            <span className="settings__label">Hospital Name</span>
            <input
              type="text"
              className="settings__input"
              value={settings.hospitalName}
              onChange={(event) => updateField("hospitalName", event.target.value)}
            />
          </label>

          <label className="settings__field settings__field--wide">
            <span className="settings__label">Address</span>
            <textarea
              className="settings__textarea"
              rows={2}
              placeholder="One line per address/contact line"
              value={settings.address}
              onChange={(event) => updateField("address", event.target.value)}
            />
          </label>

          <label className="settings__field">
            <span className="settings__label">Header Text</span>
            <input
              type="text"
              className="settings__input"
              value={settings.headerText ?? ""}
              onChange={(event) => updateField("headerText", event.target.value)}
            />
          </label>

          <label className="settings__field">
            <span className="settings__label">Footer Text</span>
            <input
              type="text"
              className="settings__input"
              value={settings.footerText ?? ""}
              onChange={(event) => updateField("footerText", event.target.value)}
            />
          </label>
        </div>
      </section>

      <div className="settings__actions">
        <button
          type="button"
          className="settings__save-button"
          onClick={handleSave}
          disabled={saveState === "saving"}
        >
          {saveState === "saving" ? <Spinner label="Saving..." /> : "Save"}
        </button>

        {saveMessage && (
          <p
            className={`settings__message ${
              saveState === "error" ? "settings__message--error" : "settings__message--success"
            }`}
          >
            {saveState === "error" ? "⚠️" : "✓"} {saveMessage}
          </p>
        )}
      </div>
    </div>
  );
}
