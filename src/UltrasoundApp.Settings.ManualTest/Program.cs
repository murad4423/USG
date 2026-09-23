// Throwaway manual test harness for SettingsService — NOT part of the
// shipped app. Per this step's scope, SettingsService isn't constructed
// anywhere in Program.cs/MainForm yet (no SettingsPage.tsx, no IPC
// handler, and it is NOT wired into DicomScpListener/ImageSheetComposer/
// SilentPrinter/report-template resolution — that's a later step) — this
// harness is the only way to exercise save/load for now.
//
// Wired against the REAL data/app.db (not an isolated throwaway
// database, unlike UltrasoundApp.Templating.ManualTest/
// UltrasoundApp.ReportRender.ManualTest) — deliberately, since Settings
// is the app's own persistent configuration: whatever you save here is
// exactly what a later step's real Settings UI (and the app itself, once
// it's wired up) will read back.
//
// Run with:
//   dotnet run --project src/UltrasoundApp.Settings.ManualTest -- save
//   dotnet run --project src/UltrasoundApp.Settings.ManualTest -- get
//
// "save" writes one fixed, clearly-labeled set of test values. "get" (or
// no argument at all) loads and prints whatever is currently saved — run
// it as a separate process from "save" (each `dotnet run` already is one)
// to prove persistence survives a restart, not just staying in memory
// within a single run. See docs/README.md ("Settings persistence") for
// the full walkthrough.

using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Services;
using UltrasoundApp.Data;
using UltrasoundApp.Data.Repositories;

Console.WriteLine("UltrasoundApp SettingsService manual test harness");
Console.WriteLine("===================================================");
Console.WriteLine();

var dbContext = new AppDbContext();
DbInitializer.Initialize(dbContext);
Console.WriteLine($"Database: {dbContext.DatabasePath}");
Console.WriteLine("(the SAME database the real app uses — not an isolated test database —");
Console.WriteLine(" so what you save here is exactly what a later step's app/UI would read back.)");
Console.WriteLine();

var settingsRepository = new SettingsRepository(dbContext);
var settingsService = new SettingsService(settingsRepository);

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "get";

switch (mode)
{
    case "save":
        Save();
        break;

    case "get":
        Console.WriteLine("Current settings (SettingsService.Get() — defaults applied if nothing was ever saved):");
        Print(settingsService.Get());
        break;

    default:
        Console.WriteLine($"Unknown argument '{args[0]}'. Expected 'save' or 'get' (or no argument, which behaves like 'get').");
        break;
}

void Save()
{
    // One fixed, clearly-labeled set of test values — every field
    // distinguishable at a glance from SettingsService's own built-in
    // defaults, so a "get" afterward makes it obvious whether it reloaded
    // exactly this or silently fell back to defaults.
    var testSettings = new AppSettings
    {
        AeTitle = "TESTAE",
        ListeningPort = 11113,
        ReportTemplatesFolderPath = @"C:\ultrasound-test\templates\reports",
        ImageSheetBrandingTemplatePath = @"C:\ultrasound-test\templates\branding.json",
        PrinterName = "Test Printer",
        LogoPath = @"C:\ultrasound-test\logo.png",
        HospitalName = "Test General Hospital",
        Address = "456 Test Avenue, Testville, TS 99999",
        HeaderText = "TEST HEADER",
        FooterText = "TEST FOOTER",
    };

    settingsService.Save(testSettings);

    Console.WriteLine("Saved:");
    Print(testSettings);
    Console.WriteLine();
    Console.WriteLine("Now run this harness again with \"get\" — as a genuinely separate process,");
    Console.WriteLine("e.g. after closing this terminal or restarting the machine, not just");
    Console.WriteLine("re-reading a variable still in memory — to confirm these exact values");
    Console.WriteLine("come back:");
    Console.WriteLine("  dotnet run --project src/UltrasoundApp.Settings.ManualTest -- get");
}

void Print(AppSettings settings)
{
    Console.WriteLine($"  AeTitle:                        {settings.AeTitle}");
    Console.WriteLine($"  ListeningPort:                  {settings.ListeningPort}");
    Console.WriteLine($"  ReportTemplatesFolderPath:      {settings.ReportTemplatesFolderPath ?? "(null)"}");
    Console.WriteLine($"  ImageSheetBrandingTemplatePath: {settings.ImageSheetBrandingTemplatePath ?? "(null)"}");
    Console.WriteLine($"  PrinterName:                    {settings.PrinterName ?? "(null)"}");
    Console.WriteLine($"  LogoPath:                       {settings.LogoPath ?? "(null)"}");
    Console.WriteLine($"  HospitalName:                   {settings.HospitalName}");
    Console.WriteLine($"  Address:                        {settings.Address}");
    Console.WriteLine($"  HeaderText:                     {settings.HeaderText ?? "(null)"}");
    Console.WriteLine($"  FooterText:                     {settings.FooterText ?? "(null)"}");
}
