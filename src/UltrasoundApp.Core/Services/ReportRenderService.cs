using System.Text.Json;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Reporting;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// Outcome of <see cref="ReportRenderService.GenerateReportAsync"/>.
/// </summary>
/// <param name="GeneratedReport">The saved (inserted or updated) <see cref="Models.GeneratedReport"/> row.</param>
/// <param name="ReportRowWasCreated"><c>true</c> if this call inserted a new row; <c>false</c> if it updated an existing one for the same study.</param>
/// <param name="MissingFields">Template placeholder tokens that had no matching value (auto-filled or manual) — surfaced so a caller can flag an incomplete report before printing.</param>
/// <param name="UnusedFields">Supplied field keys (auto-filled or manual) that didn't match any placeholder actually present in the template.</param>
public sealed record ReportRenderOutcome(
    GeneratedReport GeneratedReport,
    bool ReportRowWasCreated,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> UnusedFields);

/// <summary>
/// Generates a filled-in, rendered PDF report for a study.
///
/// <para>
/// Auto-fills DICOM-sourced fields (patient name, ID, age, sex, study
/// date) from the already-stored <see cref="Patient"/>/<see cref="Study"/>
/// rows, layers the caller-supplied exam-specific field values on top,
/// merges the result into the exam type's report template via
/// <see cref="IReportTemplateEngine"/>, renders the merged HTML to a PDF
/// via <see cref="IReportPdfRenderer"/>, and saves the filled field values
/// (as JSON) plus the PDF path into a <see cref="GeneratedReport"/> row —
/// inserting one if the study has never had a report generated before, or
/// updating the existing row (never creating a duplicate) otherwise.
/// </para>
///
/// <para>
/// This class depends only on repository/engine/renderer <b>interfaces</b>
/// that live in Core (see <see cref="IReportTemplateEngine"/> and
/// <see cref="IReportPdfRenderer"/>) — never on the concrete
/// <c>UltrasoundApp.Templating</c> or <c>UltrasoundApp.Data</c> types
/// themselves — so Core keeps its existing rule of depending on nothing
/// else in the solution. The caller (currently a manual test harness; a
/// future UI-backed step) is responsible for constructing the concrete
/// <c>TemplateEngine</c>/<c>PdfRenderer</c>/repository instances and
/// passing them in.
/// </para>
///
/// <para>
/// This service does not build any UI and does not decide where the
/// output PDF should live on disk — the caller supplies
/// <c>outputPdfPath</c> explicitly, the same way <c>PdfRenderer</c> itself
/// takes an explicit path rather than deciding one internally.
/// </para>
/// </summary>
public sealed class ReportRenderService
{
    private readonly IPatientRepository _patientRepository;
    private readonly IStudyRepository _studyRepository;
    private readonly IReportTemplateRepository _reportTemplateRepository;
    private readonly IGeneratedReportRepository _generatedReportRepository;
    private readonly IReportTemplateEngine _templateEngine;
    private readonly IReportPdfRenderer _pdfRenderer;

    public ReportRenderService(
        IPatientRepository patientRepository,
        IStudyRepository studyRepository,
        IReportTemplateRepository reportTemplateRepository,
        IGeneratedReportRepository generatedReportRepository,
        IReportTemplateEngine templateEngine,
        IReportPdfRenderer pdfRenderer)
    {
        _patientRepository = patientRepository;
        _studyRepository = studyRepository;
        _reportTemplateRepository = reportTemplateRepository;
        _generatedReportRepository = generatedReportRepository;
        _templateEngine = templateEngine;
        _pdfRenderer = pdfRenderer;
    }

    /// <summary>
    /// Generates (or re-generates) the report for <paramref name="studyInstanceUid"/>.
    /// </summary>
    /// <param name="studyInstanceUid">The study to generate a report for. Must already exist (e.g. from a prior DICOM import).</param>
    /// <param name="manualFieldValues">
    /// Exam-specific field values entered by the operator (e.g. LIVER_SIZE,
    /// IMPRESSION). These take precedence over the auto-filled DICOM
    /// fields if a key collides with one of them.
    /// </param>
    /// <param name="outputPdfPath">Full file path the rendered PDF should be written to.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the study, its owning patient, the exam type's report
    /// template, or the PDF rendering step itself cannot be found/completed.
    /// </exception>
    public async Task<ReportRenderOutcome> GenerateReportAsync(
        string studyInstanceUid,
        IReadOnlyDictionary<string, string> manualFieldValues,
        string outputPdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyInstanceUid);
        ArgumentNullException.ThrowIfNull(manualFieldValues);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);

        Study study = _studyRepository.GetById(studyInstanceUid)
            ?? throw new InvalidOperationException(
                $"No study found with StudyInstanceUID '{studyInstanceUid}'.");

        Patient patient = _patientRepository.GetById(study.PatientID)
            ?? throw new InvalidOperationException(
                $"Study '{studyInstanceUid}' references Patient '{study.PatientID}', which was not found.");

        ReportTemplate template = _reportTemplateRepository.GetByExamType(study.ExamType)
            ?? throw new InvalidOperationException(
                $"No report template is configured for exam type '{study.ExamType}'.");

        Dictionary<string, string> allFieldValues = BuildAutoFilledFields(patient, study);
        foreach ((string key, string value) in manualFieldValues)
        {
            allFieldValues[key] = value ?? string.Empty;
        }

        ReportMergeResult mergeResult = _templateEngine.Merge(template.HtmlTemplateFilePath, allFieldValues);

        ReportPdfRenderResult renderResult = await _pdfRenderer.RenderHtmlToPdfAsync(mergeResult.MergedHtml, outputPdfPath);
        if (!renderResult.Succeeded || renderResult.OutputPdfPath is null)
        {
            throw new InvalidOperationException(
                $"Failed to render report PDF for study '{studyInstanceUid}': {renderResult.Message}");
        }

        (GeneratedReport generatedReport, bool wasCreated) =
            SaveGeneratedReport(studyInstanceUid, allFieldValues, renderResult.OutputPdfPath);

        return new ReportRenderOutcome(generatedReport, wasCreated, mergeResult.MissingFields, mergeResult.UnusedFields);
    }

    /// <summary>
    /// Builds the DICOM-sourced field dictionary shared by every report
    /// template (PATIENT_NAME, PATIENT_ID, AGE, SEX, DATE) — the same
    /// token names the example templates from the previous step already use.
    /// </summary>
    private static Dictionary<string, string> BuildAutoFilledFields(Patient patient, Study study)
    {
        return new Dictionary<string, string>
        {
            ["PATIENT_NAME"] = patient.PatientName,
            ["PATIENT_ID"] = patient.PatientID,
            ["AGE"] = FormatAge(patient),
            ["SEX"] = patient.Sex ?? string.Empty,
            ["DATE"] = study.StudyDateTime.ToString("dd MMM yyyy"),
        };
    }

    /// <summary>
    /// Prefers the stored <see cref="Patient.Age"/> if present; otherwise
    /// derives an approximate age in years from <see cref="Patient.DateOfBirth"/>.
    /// Returns an empty string if neither is available.
    /// </summary>
    private static string FormatAge(Patient patient)
    {
        if (patient.Age is int age)
        {
            return $"{age} Y";
        }

        if (patient.DateOfBirth is DateTime dob)
        {
            int years = DateTime.Today.Year - dob.Year;
            if (dob.Date > DateTime.Today.AddYears(-years))
            {
                years--;
            }

            return $"{years} Y";
        }

        return string.Empty;
    }

    /// <summary>
    /// Inserts a new <see cref="GeneratedReport"/> row for this study, or
    /// updates the existing one — a study only ever has one
    /// <see cref="GeneratedReport"/> row (re-generating a report edits it
    /// in place rather than creating a duplicate), matching how
    /// <c>DicomProcessingService</c> keeps Patient/Study rows unique.
    /// </summary>
    private (GeneratedReport GeneratedReport, bool WasCreated) SaveGeneratedReport(
        string studyInstanceUid,
        Dictionary<string, string> fieldValues,
        string generatedPdfPath)
    {
        string fieldValuesJson = JsonSerializer.Serialize(fieldValues);
        DateTime now = DateTime.UtcNow;

        GeneratedReport? existing = _generatedReportRepository.GetByStudy(studyInstanceUid);
        if (existing is not null)
        {
            existing.FieldValuesJson = fieldValuesJson;
            existing.GeneratedPdfPath = generatedPdfPath;
            existing.ModifiedAt = now;
            _generatedReportRepository.Update(existing);
            return (existing, false);
        }

        var report = new GeneratedReport
        {
            StudyInstanceUID = studyInstanceUid,
            FieldValuesJson = fieldValuesJson,
            GeneratedPdfPath = generatedPdfPath,
            CreatedAt = now,
            ModifiedAt = now,
        };
        _generatedReportRepository.Add(report);
        return (report, true);
    }
}
