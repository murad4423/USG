// Throwaway manual test harness for ReportRenderService — NOT part of the
// shipped app. Exercises the full backend pipeline directly (auto-fill from
// stored Patient/Study rows, merge with manual exam fields including a
// Bangla string, render to PDF, save the GeneratedReport row), the way the
// task for this step asked ("test ReportRenderService directly — no
// ReportPrintPage, no ReportForm yet").
//
// Uses its own SQLite database file (data/manual-test-report-render.db),
// completely separate from the real app.db, so running this repeatedly
// never touches real patient data.
//
// Run with:
//   dotnet run --project src/UltrasoundApp.ReportRender.ManualTest
//
// See docs/README.md ("Report generation backend") for the full walkthrough
// of what to check in the console output and the generated PDF/database row.

using System.Text;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Services;
using UltrasoundApp.Data;
using UltrasoundApp.Data.Repositories;
using UltrasoundApp.Templating;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("UltrasoundApp ReportRenderService manual test harness");
Console.WriteLine("======================================================");
Console.WriteLine();

// --- Set up an isolated test database (never touches data/app.db) ---
string testDbPath = Path.Combine(AppPaths.GetDataDirectory(), "manual-test-report-render.db");
Console.WriteLine($"Test database: {testDbPath}");

var dbContext = new AppDbContext(testDbPath);
DbInitializer.Initialize(dbContext);

var patientRepository = new PatientRepository(dbContext);
var studyRepository = new StudyRepository(dbContext);
var reportTemplateRepository = new ReportTemplateRepository(dbContext);
var generatedReportRepository = new GeneratedReportRepository(dbContext);

// --- Seed a test Patient + Study + ReportTemplate row (idempotent — safe to re-run) ---
const string patientId = "MT-0001";
const string studyInstanceUid = "1.2.3.MANUALTEST.REPORTRENDER.1";
const string examType = "WholeAbdomen";

if (patientRepository.GetById(patientId) is null)
{
    patientRepository.Add(new Patient
    {
        PatientID = patientId,
        PatientName = "রহিমা খাতুন", // Bangla — deliberately testing non-Latin auto-fill
        Age = 34,
        Sex = "Female",
    });
    Console.WriteLine($"Seeded test patient '{patientId}'.");
}
else
{
    Console.WriteLine($"Test patient '{patientId}' already exists — reusing.");
}

if (studyRepository.GetById(studyInstanceUid) is null)
{
    studyRepository.Add(new Study
    {
        StudyInstanceUID = studyInstanceUid,
        PatientID = patientId,
        ExamType = examType,
        StudyDateTime = DateTime.Now,
        Status = "Received",
    });
    Console.WriteLine($"Seeded test study '{studyInstanceUid}'.");
}
else
{
    Console.WriteLine($"Test study '{studyInstanceUid}' already exists — reusing.");
}

string templateFilePath = TemplatingPaths.GetReportTemplatePath("whole-abdomen.html");
if (reportTemplateRepository.GetByExamType(examType) is null)
{
    reportTemplateRepository.Add(new ReportTemplate
    {
        ExamType = examType,
        HtmlTemplateFilePath = templateFilePath,
        PlaceholderFields = new List<string>(), // not consulted by ReportRenderService — TemplateEngine scans the template itself
    });
    Console.WriteLine($"Seeded ReportTemplate row for exam type '{examType}' -> {templateFilePath}");
}
else
{
    Console.WriteLine($"ReportTemplate row for exam type '{examType}' already exists — reusing.");
}

Console.WriteLine();

// --- Wire up ReportRenderService with the real Templating classes ---
// TemplateEngine and PdfRenderer implement Core's IReportTemplateEngine /
// IReportPdfRenderer (explicit interface implementation), so they can be
// passed straight in without any adapter class.
Console.WriteLine("Starting headless WebView2 host (this can take a few seconds the first time)...");
using var pdfRenderer = new PdfRenderer();
Console.WriteLine("WebView2 host ready.");
Console.WriteLine();

var templateEngine = new TemplateEngine();
var reportRenderService = new ReportRenderService(
    patientRepository,
    studyRepository,
    reportTemplateRepository,
    generatedReportRepository,
    templateEngine,
    pdfRenderer);

// Exam-specific fields an operator would type in — everything the
// whole-abdomen template needs beyond the DICOM-sourced auto-fill fields
// (PATIENT_NAME/PATIENT_ID/AGE/SEX/DATE), including a Bangla sentence.
var manualFields = new Dictionary<string, string>
{
    ["REFERRING_PHYSICIAN"] = "Dr. Kamal Hossain",
    ["LIVER_SIZE"] = "13.2 cm",
    ["LIVER_ECHOTEXTURE"] = "Normal, homogeneous",
    ["GALLBLADDER"] = "Normal wall thickness, no calculus",
    ["CBD"] = "4 mm, not dilated",
    ["PANCREAS"] = "Normal size and echotexture",
    ["SPLEEN_SIZE"] = "10.8 cm",
    ["RIGHT_KIDNEY_SIZE"] = "10.1 x 4.6 cm",
    ["LEFT_KIDNEY_SIZE"] = "9.9 x 4.5 cm",
    ["URINARY_BLADDER"] = "Well distended, normal wall",
    ["ASCITES"] = "None",
    // Bangla sentence inside a longer free-text field — the harder case,
    // since it must wrap correctly inside the impression box.
    ["IMPRESSION"] = "স্বাভাবিক পেটের আল্ট্রাসনোগ্রাফি রিপোর্ট। কোনো উল্লেখযোগ্য অস্বাভাবিকতা পাওয়া যায়নি।",
    ["SONOLOGIST_NAME"] = "Dr. Nasrin Akter",
};

string outputPdfPath = Path.Combine(
    TemplatingPaths.GetGeneratedReportsScratchDirectory(),
    "test-report-render-service.pdf");

Console.WriteLine($"Generating report for study '{studyInstanceUid}'...");
ReportRenderOutcome outcome = await reportRenderService.GenerateReportAsync(studyInstanceUid, manualFields, outputPdfPath);

PrintOutcome(outcome, isFirstRun: true);

// --- Run it a second time to prove re-generation UPDATES the existing ---
// --- GeneratedReport row rather than creating a duplicate. ---
Console.WriteLine();
Console.WriteLine("Generating the SAME report again (should UPDATE, not duplicate)...");
var secondManualFields = new Dictionary<string, string>(manualFields)
{
    ["IMPRESSION"] = manualFields["IMPRESSION"] + " (revised)",
};
ReportRenderOutcome secondOutcome = await reportRenderService.GenerateReportAsync(studyInstanceUid, secondManualFields, outputPdfPath);
PrintOutcome(secondOutcome, isFirstRun: false);

Console.WriteLine();
Console.WriteLine($"All GeneratedReport rows currently in the test database: {generatedReportRepository.GetAll().Count}");
Console.WriteLine("(should be exactly 1 — the second run updated the same row instead of adding another).");

Console.WriteLine();
Console.WriteLine($"Open the PDF and confirm: {outputPdfPath}");
Console.WriteLine("  - Patient name 'রহিমা খাতুন' (auto-filled) renders as correct Bengali script.");
Console.WriteLine("  - The Bangla IMPRESSION sentence (manual field) renders correctly, with '(revised)' appended.");
Console.WriteLine("  - AGE shows '34 Y', SEX shows 'Female', DATE shows today's date — all auto-filled, none typed manually.");
Console.WriteLine("  - Zero MissingFields and zero UnusedFields were reported above for both runs.");

static void PrintOutcome(ReportRenderOutcome outcome, bool isFirstRun)
{
    GeneratedReport report = outcome.GeneratedReport;
    Console.WriteLine($"  ReportRowWasCreated: {outcome.ReportRowWasCreated} (expected {(isFirstRun ? "True" : "False")})");
    Console.WriteLine($"  MissingFields: {(outcome.MissingFields.Count == 0 ? "(none)" : string.Join(", ", outcome.MissingFields))}");
    Console.WriteLine($"  UnusedFields:  {(outcome.UnusedFields.Count == 0 ? "(none)" : string.Join(", ", outcome.UnusedFields))}");
    Console.WriteLine($"  GeneratedReport.StudyInstanceUID: {report.StudyInstanceUID}");
    Console.WriteLine($"  GeneratedReport.GeneratedPdfPath: {report.GeneratedPdfPath}");
    Console.WriteLine($"  GeneratedReport.CreatedAt:  {report.CreatedAt:o}");
    Console.WriteLine($"  GeneratedReport.ModifiedAt: {report.ModifiedAt:o}");
    Console.WriteLine($"  PDF exists on disk: {(report.GeneratedPdfPath is not null && File.Exists(report.GeneratedPdfPath))}");
}
