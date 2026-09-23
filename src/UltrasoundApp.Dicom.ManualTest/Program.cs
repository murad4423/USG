// Throwaway manual test harness for DicomScpListener — NOT part of the
// shipped app. As of the auto-start step, UltrasoundApp.Host's own
// Program.cs starts a DicomScpListener automatically on the same default
// port, so this harness is no longer the only way to exercise the
// listener — it's still useful for testing in isolation (no need to
// launch the whole WinForms app) or on a different port (its second
// command-line argument) so it doesn't collide with an already-running
// app instance. This was the "temporary manual test call" the DicomScpListener
// task originally asked for, so the listener could be verified before
// anything else depended on it.
//
// Unlike UltrasoundApp.Templating.ManualTest/UltrasoundApp.ReportRender.ManualTest
// (which use an isolated throwaway database), this harness deliberately
// wires DicomFileParser/DicomProcessingService up against the SAME real
// data/app.db the manual upload path (WebViewBridge.HandleUploadDicom)
// and the app's own auto-started listener use. That is the whole point:
// a study pushed to the listener must be processed identically to one
// imported by hand, so pointing both at the same database lets you push
// a test file here and then see it appear in the actual app's Patient
// List, exactly the way a manual upload would — a direct side-by-side
// comparison, not just a console log claiming it worked.
//
// Run with:
//   dotnet run --project src/UltrasoundApp.Dicom.ManualTest
//   dotnet run --project src/UltrasoundApp.Dicom.ManualTest -- MYAE 11113   (override AE Title/port)
//
// Then, from another terminal (or another machine on the same network),
// push a .dcm file at it with a real C-STORE SCU tool (dcm4che's
// storescu, or fo-dicom's own DicomClient). See docs/README.md
// ("Auto-receive DICOM SCP listener") for the full walkthrough, including
// exactly how to test this without a physical ultrasound machine.

using UltrasoundApp.Data;
using UltrasoundApp.Data.Repositories;
using UltrasoundApp.Dicom;

Console.WriteLine("UltrasoundApp DicomScpListener manual test harness");
Console.WriteLine("===================================================");
Console.WriteLine();

var dbContext = new AppDbContext();
DbInitializer.Initialize(dbContext);
Console.WriteLine($"Database: {dbContext.DatabasePath}");
Console.WriteLine("(the SAME database the real app and the Manual DICOM Upload button use —");
Console.WriteLine(" not an isolated test database — so results are directly comparable.)");
Console.WriteLine();

var patientRepository = new PatientRepository(dbContext);
var studyRepository = new StudyRepository(dbContext);
var imageRepository = new ImageRepository(dbContext);

// The exact same DicomFileParser/DicomProcessingService types (and, since
// they're stateless wrappers over the repositories above, the same
// effective pipeline) that WebViewBridge.HandleUploadDicom wires up for a
// manually-selected file — see DicomScpListener's class doc-comment for
// why passing these in rather than letting the listener construct its own
// is what guarantees one shared extraction/storage path, not two.
var dicomFileParser = new DicomFileParser();
var dicomProcessingService = new DicomProcessingService(patientRepository, studyRepository, imageRepository);

// Hardcoded defaults per this step's scope (see DicomScpListener.DefaultAeTitle/
// DefaultPort) — optionally overridden from the command line so you can, e.g.,
// run two instances on different ports without editing code.
string aeTitle = args.Length > 0 ? args[0] : DicomScpListener.DefaultAeTitle;
int port = DicomScpListener.DefaultPort;
if (args.Length > 1 && !int.TryParse(args[1], out port))
{
    Console.WriteLine($"'{args[1]}' is not a valid port number — using the default {DicomScpListener.DefaultPort} instead.");
    port = DicomScpListener.DefaultPort;
}

using var listener = new DicomScpListener(dicomFileParser, dicomProcessingService, aeTitle, port);

Console.WriteLine($"Starting listener: AE Title '{aeTitle}', port {port}...");
Console.WriteLine();
bool started = listener.Start();

if (!started)
{
    // DicomScpListener already logged the specific reason (e.g. "port
    // already in use") via its own logError callback above — nothing more
    // to add here beyond exiting cleanly instead of trying to proceed
    // with no listener running.
    Console.WriteLine();
    Console.WriteLine("Listener failed to start — see the error above. Exiting.");
    return;
}

Console.WriteLine();
Console.WriteLine("Listener is running and waiting for an incoming C-STORE association.");
Console.WriteLine("From another terminal (or another machine on the same network), push a");
Console.WriteLine("test .dcm file at it, e.g. with dcm4che's storescu:");
Console.WriteLine();
Console.WriteLine($"  storescu -c {aeTitle}@<this-machine-ip-or-localhost>:{port} path\\to\\test.dcm");
Console.WriteLine();
Console.WriteLine("Watch this console for \"Association accepted\" / \"Received SOP Instance\"");
Console.WriteLine("messages, then check the app's Patient List (or query data/app.db directly)");
Console.WriteLine("to confirm it was grouped/deduplicated exactly like a manual upload.");
Console.WriteLine();
Console.WriteLine("Press Enter to stop the listener and exit.");
Console.ReadLine();

listener.Stop();
Console.WriteLine("Stopped.");
