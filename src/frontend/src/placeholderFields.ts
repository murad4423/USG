import type { CSSProperties } from "react";
import type { PatientStudyRecord } from "./ipc";
import type { TemplatePlaceholder } from "./ipcImageTemplate";

/**
 * Patient-information fields the operator can place on the image template
 * (Settings -> Select Image Area -> "Add patient information" dropdown).
 *
 * `key` is what is saved with each placeholder and what the host looks up
 * when printing (WebViewBridge.BuildPlaceholderValues uses the SAME keys for
 * "Reprint Images"), so do not rename a key without changing it there too.
 * `sample` is only shown inside the editor so the operator can see roughly
 * how big the real text will be.
 */
export interface PlaceholderFieldDef {
  key: string;
  label: string;
  sample: string;
}

export const PLACEHOLDER_FIELDS: PlaceholderFieldDef[] = [
  { key: "patientName", label: "Patient Name", sample: "MAHMUDA AKTER" },
  { key: "patientId", label: "Patient ID", sample: "E51289-26-09-18-3" },
  { key: "age", label: "Age", sample: "32" },
  { key: "sex", label: "Sex", sample: "F" },
  { key: "ageSex", label: "Age / Sex", sample: "32 / F" },
  { key: "dateOfBirth", label: "Date of Birth", sample: "14/03/1994" },
  { key: "examType", label: "Exam Type", sample: "Whole Abdomen" },
  { key: "studyDate", label: "Study Date", sample: "18/09/2026" },
  { key: "studyTime", label: "Study Time", sample: "11:10 AM" },
  { key: "studyDateTime", label: "Study Date & Time", sample: "18/09/2026 11:10 AM" },
  { key: "printDate", label: "Print Date (today)", sample: "20/09/2026" }
];

export function fieldLabel(key: string): string {
  return PLACEHOLDER_FIELDS.find((field) => field.key === key)?.label ?? key;
}

export function fieldSample(key: string): string {
  return PLACEHOLDER_FIELDS.find((field) => field.key === key)?.sample ?? fieldLabel(key);
}

/**
 * Fallback font size for placeholders that have no explicit `fontSize`
 * (saved by the first version): box height x this ratio. MUST match
 * PlaceholderFontHeightRatio in ImageSheetComposer.cs.
 */
export const PLACEHOLDER_FONT_RATIO = 0.62;

/** A4 height in points — the composer's default page. Font sizes are in points too. */
export const PAGE_HEIGHT_POINTS = 841.89;
export const MIN_FONT_POINTS = 4;
export const MAX_FONT_POINTS = 72;

/** Font used when a placeholder has no font chosen (same list the printed page falls back to). */
const DEFAULT_FONT_STACK = '"Segoe UI", "Nirmala UI", "Noto Sans Bengali", sans-serif';

/**
 * Fonts offered in the editor's "Font" dropdown. They are standard Windows
 * fonts, so the printed PDF finds them by name just like the preview does.
 * "" = the app's default font. Pick "Nirmala UI" for Bangla text.
 */
export const PLACEHOLDER_FONT_FAMILIES: { value: string; label: string }[] = [
  { value: "", label: "Default (Segoe UI)" },
  { value: "Arial", label: "Arial" },
  { value: "Calibri", label: "Calibri" },
  { value: "Cambria", label: "Cambria" },
  { value: "Georgia", label: "Georgia" },
  { value: "Times New Roman", label: "Times New Roman" },
  { value: "Verdana", label: "Verdana" },
  { value: "Tahoma", label: "Tahoma" },
  { value: "Trebuchet MS", label: "Trebuchet MS" },
  { value: "Courier New", label: "Courier New" },
  { value: "Consolas", label: "Consolas" },
  { value: "Nirmala UI", label: "Nirmala UI (Bangla)" }
];

export const DEFAULT_PLACEHOLDER_FONT_POINTS = 11;
export const DEFAULT_PLACEHOLDER_COLOR = "#000000";

/** Returns the colour if it is a "#RRGGBB" string, otherwise null. */
export function validColor(value: string | undefined): string | null {
  return value && /^#[0-9a-fA-F]{6}$/.test(value) ? value.toLowerCase() : null;
}

/** The font size (pt) the placeholder really prints at. */
export function effectiveFontPoints(placeholder: TemplatePlaceholder): number {
  const raw =
    placeholder.fontSize !== undefined && placeholder.fontSize > 0
      ? placeholder.fontSize
      : placeholder.height * PAGE_HEIGHT_POINTS * PLACEHOLDER_FONT_RATIO;
  return Math.min(MAX_FONT_POINTS, Math.max(MIN_FONT_POINTS, raw));
}

/** Smallest box height (page fraction) that comfortably holds one line at `points`. */
export function minBoxHeightForFont(points: number): number {
  return (points * 1.3) / PAGE_HEIGHT_POINTS;
}

const pad2 = (value: number) => String(value).padStart(2, "0");

function formatDate(date: Date): string {
  return `${pad2(date.getDate())}/${pad2(date.getMonth() + 1)}/${date.getFullYear()}`;
}

function formatTime(date: Date): string {
  const hours24 = date.getHours();
  const hours12 = hours24 % 12 === 0 ? 12 : hours24 % 12;
  return `${pad2(hours12)}:${pad2(date.getMinutes())} ${hours24 < 12 ? "AM" : "PM"}`;
}

/** Parses "yyyy-MM-dd" (what the host sends for dateOfBirth) as a local date. */
function parseIsoDate(value: string | undefined): Date | null {
  if (!value) {
    return null;
  }
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) {
    return null;
  }
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isNaN(date.getTime()) ? null : date;
}

function ageFrom(study: PatientStudyRecord, dateOfBirth: Date | null): string {
  if (study.age !== undefined && study.age !== null) {
    return String(study.age);
  }
  if (!dateOfBirth) {
    return "";
  }
  const today = new Date();
  let years = today.getFullYear() - dateOfBirth.getFullYear();
  const hadBirthday =
    today.getMonth() > dateOfBirth.getMonth() ||
    (today.getMonth() === dateOfBirth.getMonth() && today.getDate() >= dateOfBirth.getDate());
  if (!hadBirthday) {
    years -= 1;
  }
  return years >= 0 ? String(years) : "";
}

/**
 * The text every placeholder field prints for this patient/study
 * (fieldKey -> value). Fields the patient doesn't have come back as "" and
 * simply print nothing.
 */
export function buildPlaceholderValues(study: PatientStudyRecord | null): Record<string, string> {
  if (!study) {
    return {};
  }

  const studyDate = new Date(study.studyDateTime);
  const studyDateValid = !Number.isNaN(studyDate.getTime());
  const dateOfBirth = parseIsoDate(study.dateOfBirth);
  const age = ageFrom(study, dateOfBirth);
  const sex = study.sex ?? "";
  const studyDateText = studyDateValid ? formatDate(studyDate) : "";
  const studyTimeText = studyDateValid ? formatTime(studyDate) : "";

  return {
    patientName: study.patientName ?? "",
    patientId: study.patientId ?? "",
    age,
    sex,
    ageSex: [age, sex].filter((part) => part !== "").join(" / "),
    dateOfBirth: dateOfBirth ? formatDate(dateOfBirth) : "",
    examType: study.examType ?? "",
    studyDate: studyDateText,
    studyTime: studyTimeText,
    studyDateTime: [studyDateText, studyTimeText].filter((part) => part !== "").join(" "),
    printDate: formatDate(new Date())
  };
}

/** Position + size of a placeholder as percentages of the page (its containing block). */
export function placeholderBoxStyle(placeholder: TemplatePlaceholder): CSSProperties {
  return {
    position: "absolute",
    left: `${placeholder.x * 100}%`,
    top: `${placeholder.y * 100}%`,
    width: `${placeholder.width * 100}%`,
    height: `${placeholder.height * 100}%`
  };
}

/**
 * Text look for a placeholder drawn on a page that is `pageHeightPx` tall on
 * screen (any zoom). Single line, vertically centred, cut off with "…" when
 * it doesn't fit the box — the printed PDF does the same. Font size, font
 * family and colour come from the placeholder itself.
 */
export function placeholderTextStyle(placeholder: TemplatePlaceholder, pageHeightPx: number): CSSProperties {
  const pxPerPoint = pageHeightPx / PAGE_HEIGHT_POINTS;
  const family = placeholder.fontFamily?.replace(/["\\]/g, "").trim();

  return {
    width: "100%",
    height: "100%",
    overflow: "hidden",
    whiteSpace: "nowrap",
    textOverflow: "ellipsis",
    boxSizing: "border-box",
    fontFamily: family ? `"${family}", ${DEFAULT_FONT_STACK}` : DEFAULT_FONT_STACK,
    fontSize: `${effectiveFontPoints(placeholder) * pxPerPoint}px`,
    lineHeight: `${placeholder.height * pageHeightPx}px`,
    fontWeight: placeholder.bold ? 700 : 400,
    textAlign: placeholder.align ?? "left",
    color: validColor(placeholder.color) ?? DEFAULT_PLACEHOLDER_COLOR
  };
}
