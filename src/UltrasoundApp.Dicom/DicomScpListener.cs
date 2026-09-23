using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Services;

namespace UltrasoundApp.Dicom;

/// <summary>
/// Persistent background C-STORE SCP (DICOM listener): accepts incoming
/// associations on a configurable AE Title/port and, for every image
/// pushed to it, runs it through the EXACT SAME
/// <see cref="DicomFileParser"/> -&gt; <see cref="DicomProcessingService"/>
/// pipeline the manual upload path uses (see
/// <c>UltrasoundApp.Host.WebViewBridge.HandleUploadDicom</c>) — there is no
/// separate/duplicated extraction or storage logic for the auto-receive
/// path. Because both paths funnel through the same
/// <see cref="DicomProcessingService"/> (itself keyed off DICOM's own
/// Patient/Study/SOP Instance UIDs, not upload/session state), a study
/// pushed here is grouped and deduplicated identically to one imported by
/// hand — including against a study that already exists from a manual
/// upload, or a re-push of the same instance.
///
/// The one difference from the manual path: a manually uploaded file
/// already sits on disk (wherever the operator picked it from), but an
/// incoming C-STORE image only exists in memory (fo-dicom's default
/// receive behavior stages it in a temp file that's deleted once
/// <see cref="OnCStoreRequestAsync"/>-equivalent handling returns), so
/// this class first saves each received instance under
/// <see cref="ImageStoragePaths.GetDicomIncomingDirectory"/> — giving
/// <see cref="DicomFileParser.Parse"/> the same kind of "a .dcm file that
/// exists on disk" input either path provides.
///
/// Wired into automatic app startup as of the auto-start step — see
/// <c>UltrasoundApp.Host.Program.Main</c>, which constructs this class
/// with the same <see cref="DicomFileParser"/>/<see cref="DicomProcessingService"/>
/// instances Manual DICOM Upload uses and calls <see cref="Start"/> before
/// the main window opens. A failed <see cref="Start"/> (e.g. the port is
/// already in use) is logged and otherwise ignored there — see
/// <see cref="Start"/>'s own doc-comment for why that's safe: it never
/// throws, and leaves this instance in the same well-defined "not
/// running" state whether it was never started or failed to bind, so
/// nothing downstream needs to special-case a startup failure.
///
/// As of this step, AE Title/port are also changeable at runtime — see
/// <see cref="Restart"/>, which <c>UltrasoundApp.Host.WebViewBridge</c>'s
/// "saveSettings" handler calls when the Settings screen's AE Title/port
/// differ from what this instance is currently running with, so a
/// change takes effect immediately rather than only after the next app
/// launch. The console harness in <c>UltrasoundApp.Dicom.ManualTest</c>
/// remains useful for testing against a *different* port than whatever
/// the running app is already bound to. See docs/README.md ("Settings UI
/// + wiring") for how to exercise all of this.
/// </summary>
public sealed class DicomScpListener : IDisposable
{
    /// <summary>
    /// Hardcoded fallback AE Title, used only if Settings has never been
    /// saved yet (see <c>SettingsService.Get()</c>'s own built-in
    /// defaults, which match this value) — once Settings exist,
    /// <c>Program.cs</c> constructs this listener with whatever AE
    /// Title/port are actually saved there instead. For now this (and
    /// <see cref="DefaultPort"/>) are the "reasonable defaults" the task
    /// for this step asked for.
    /// </summary>
    public const string DefaultAeTitle = "ULTRASOUNDAPP";

    /// <summary>
    /// 11112 rather than the standard DICOM port 104: on Windows/Linux,
    /// binding to 104 requires administrator/root privileges (it's a
    /// "well-known" port below 1024), which would make this fail to bind
    /// by default on exactly the kind of dev/test machine this step needs
    /// to run on. 11112 is the conventional DICOM test-tooling port for
    /// this reason (both dcm4che's storescp/storescu and fo-dicom's own
    /// samples default to it) and needs no elevated privileges.
    /// </summary>
    public const int DefaultPort = 11112;

    // How long to wait after DicomServerFactory.Create() before checking
    // whether the bind actually succeeded. fo-dicom's DicomServer binds
    // the listening socket on a background task rather than inside
    // Create() itself, so a failure like "port already in use" does NOT
    // throw synchronously out of Create() — it shows up shortly
    // afterward as IsListening == false / a populated Exception
    // property. This delay is a pragmatic way to surface that failure
    // from a synchronous Start() call instead of leaving the caller to
    // poll for it themselves.
    private static readonly TimeSpan BindCheckDelay = TimeSpan.FromMilliseconds(300);

    private readonly DicomFileParser _dicomFileParser;
    private readonly DicomProcessingService _dicomProcessingService;
    private const string LogCategory = "DICOM-SCP";

    private readonly Action<string> _logInfo;
    private readonly Action<string> _logError;

    private IDicomServer? _server;

    /// <param name="dicomFileParser">
    /// The SAME instance the manual upload path uses (or one wired to the
    /// same Core repositories) — this class deliberately takes it as a
    /// dependency rather than constructing its own, so there is exactly
    /// one extraction pipeline in the app, not two.
    /// </param>
    /// <param name="dicomProcessingService">
    /// The SAME instance the manual upload path uses (i.e. wired to the
    /// same repositories/database) — for auto-received and manually
    /// uploaded studies to be grouped/deduplicated consistently, both
    /// paths must ultimately write through the same service instance.
    /// </param>
    /// <param name="aeTitle">Called AE Title this listener accepts associations for. Hardcoded default: <see cref="DefaultAeTitle"/>.</param>
    /// <param name="port">TCP port to listen on. Hardcoded default: <see cref="DefaultPort"/>.</param>
    /// <param name="logInfo">Called for routine status messages (listener started, image received/processed). Defaults to <see cref="Console.WriteLine(string)"/>.</param>
    /// <param name="logError">Called for anything that stopped an operation (bind failure, a bad incoming file). Defaults to writing to <see cref="Console.Error"/>.</param>
    public DicomScpListener(
        DicomFileParser dicomFileParser,
        DicomProcessingService dicomProcessingService,
        string aeTitle = DefaultAeTitle,
        int port = DefaultPort,
        Action<string>? logInfo = null,
        Action<string>? logError = null)
    {
        _dicomFileParser = dicomFileParser;
        _dicomProcessingService = dicomProcessingService;
        AeTitle = aeTitle;
        Port = port;
        // Defaults now write to data/logs/ as well as the console, so an
        // auto-receive failure on a clinic machine (where nobody is
        // watching a terminal) is still diagnosable after the fact. An
        // explicit callback passed by a caller replaces this entirely —
        // the manual-test harness relies on that.
        _logInfo = logInfo ?? (message =>
        {
            Console.WriteLine($"[DicomScpListener] {message}");
            AppLog.Info(LogCategory, message);
        });
        _logError = logError ?? (message =>
        {
            Console.Error.WriteLine($"[DicomScpListener] ERROR: {message}");
            AppLog.Error(LogCategory, message);
        });
    }

    /// <summary>
    /// Raised after an incoming image has been saved to the database (a new image or a new study).
    /// Raised on a background thread — a subscriber that touches the UI must marshal to the UI thread itself.
    /// The host uses it to tell the Patient List to refresh at once.
    /// </summary>
    public event Action? StudyReceived;

    /// <summary>Called AE Title this listener accepts associations for. Changed only via <see cref="Restart"/>.</summary>
    public string AeTitle { get; private set; }

    /// <summary>TCP port this listener binds to. Changed only via <see cref="Restart"/>.</summary>
    public int Port { get; private set; }

    /// <summary>True once <see cref="Start"/> has bound the port successfully and not yet been <see cref="Stop"/>ped.</summary>
    public bool IsRunning => _server is { IsListening: true };

    /// <summary>
    /// Why the most recent <see cref="Start"/>/<see cref="Restart"/> failed,
    /// or null if the last attempt succeeded. Already written to
    /// <c>data/logs/</c> — this property exists so the Settings screen can
    /// show the operator the specific reason (e.g. "port 11112 may already
    /// be in use") rather than a generic "auto-receive is down".
    /// </summary>
    public string? LastStartError { get; private set; }

    /// <summary>
    /// Binds the listening socket and starts accepting associations.
    /// Returns true on success. On failure (most commonly the port
    /// already being in use by another process — including a previous,
    /// un-<see cref="Stop"/>ped run of this same listener) this logs a
    /// clear error via the <c>logError</c> callback passed to the
    /// constructor and returns false — it never throws or crashes the
    /// host process.
    /// </summary>
    public bool Start()
    {
        if (_server is not null)
        {
            _logInfo("Start() called but the listener is already running — ignoring.");
            return true;
        }

        FoDicomBootstrapper.EnsureInitialized();

        try
        {
            // fo-dicom constructs one CStoreProvider instance per incoming
            // association itself (via DicomServerFactory.Create<T>()) —
            // it has no way to know about *this* listener's
            // DicomFileParser/DicomProcessingService/AeTitle/logging, so
            // each instance reaches back to the listener that's currently
            // running via this static handoff. Set before Create() so
            // it's guaranteed to be in place before the first connection
            // can possibly arrive.
            CStoreProvider.ActiveListener = this;

            _server = DicomServerFactory.Create<CStoreProvider>(Port);
        }
        catch (Exception ex)
        {
            // A handful of failures (e.g. an invalid port number) do
            // throw synchronously out of Create() itself — handle those
            // here too rather than only relying on the IsListening check
            // below.
            LastStartError = $"Could not start the DICOM listener on port {Port}: {ex.Message}";
            _logError(LastStartError);
            CStoreProvider.ActiveListener = null;
            _server = null;
            return false;
        }

        // See BindCheckDelay's comment: a bind failure (e.g. port already
        // in use) surfaces asynchronously, not as an exception from
        // Create() above, so give it a brief moment to either come up
        // cleanly or fail before reporting success.
        Thread.Sleep(BindCheckDelay);

        if (!_server.IsListening)
        {
            string reason = _server.Exception?.Message
                ?? $"Port {Port} may already be in use by another application (including a previous, un-stopped run of this listener).";
            LastStartError = $"Failed to bind the DICOM listener to port {Port}: {reason}";
            _logError(LastStartError);

            _server.Dispose();
            _server = null;
            CStoreProvider.ActiveListener = null;
            return false;
        }

        LastStartError = null;
        _logInfo($"Listening for C-STORE on port {Port} (AE Title '{AeTitle}').");
        return true;
    }

    /// <summary>Stops listening and releases the port. Safe to call whether or not <see cref="Start"/> succeeded.</summary>
    public void Stop()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            _server.Stop();
        }
        finally
        {
            _server.Dispose();
            _server = null;
            CStoreProvider.ActiveListener = null;
            _logInfo("Listener stopped.");
        }
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// Stops the listener (if running) and starts it again with a new AE
    /// Title/port — this is what lets changing AE Title/port in the
    /// Settings screen take effect immediately, without restarting the
    /// app. Returns exactly what the new <see cref="Start"/> call
    /// returns: true if the new values bound successfully, false if they
    /// didn't (e.g. the new port is already in use) — a failed rebind is
    /// logged the same way a failed initial <see cref="Start"/> is and
    /// leaves this instance in the same well-defined "not running" state,
    /// never a crash and never left silently listening on the old
    /// AeTitle/Port.
    /// </summary>
    public bool Restart(string aeTitle, int port)
    {
        Stop();
        AeTitle = aeTitle;
        Port = port;
        return Start();
    }

    /// <summary>
    /// Saves one received SOP Instance to <c>data/dicom-incoming/</c> and
    /// runs it through DicomFileParser -&gt; DicomProcessingService —
    /// exactly the two calls
    /// <c>UltrasoundApp.Host.WebViewBridge.HandleUploadDicom</c> makes for
    /// a manually-selected file, so there is one extraction/storage code
    /// path shared by both, not two.
    /// </summary>
    private DicomCStoreResponse ProcessReceivedFile(DicomCStoreRequest request)
    {
        string sopInstanceUid = request.SOPInstanceUID?.UID ?? Guid.NewGuid().ToString("N");

        try
        {
            string incomingDirectory = ImageStoragePaths.GetDicomIncomingDirectory();
            string savedFilePath = Path.Combine(incomingDirectory, $"{sopInstanceUid}.dcm");
            request.File.Save(savedFilePath);

            DicomParseResult parsed = _dicomFileParser.Parse(savedFilePath);
            DicomProcessingResult processed = _dicomProcessingService.Process(
                parsed.Patient, parsed.Study, parsed.Image);

            _logInfo(
                $"Received SOP Instance '{sopInstanceUid}' -> Study '{processed.Study.StudyInstanceUID}' " +
                $"(Patient '{processed.Patient.PatientID}'). " +
                $"Patient {(processed.PatientWasCreated ? "created" : "existing")}, " +
                $"Study {(processed.StudyWasCreated ? "created" : "existing")}, " +
                $"Image {(processed.ImageWasCreated ? "added" : "already present — skipped, no duplicate")}.");

            if (processed.ImageWasCreated || processed.StudyWasCreated)
            {
                try
                {
                    StudyReceived?.Invoke();
                }
                catch (Exception notifyEx)
                {
                    // A faulty subscriber must never make the machine think the image failed to store.
                    _logError($"StudyReceived handler failed: {notifyEx.Message}");
                }
            }

            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            // Same failure modes HandleUploadDicom already anticipates for
            // a manually-selected file (a DICOM file missing an
            // identifier DicomProcessingService requires, an I/O error
            // saving it, etc). Reject only this one SOP Instance —
            // never let a single bad incoming file crash the listener or
            // take down the association, so the rest of a multi-image
            // push (or later pushes) still go through.
            _logError($"Failed to process incoming SOP Instance '{sopInstanceUid}': {ex.Message}");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    /// <summary>
    /// The actual fo-dicom service class fo-dicom instantiates per
    /// incoming association. Kept private/nested — nothing outside
    /// <see cref="DicomScpListener"/> ever needs to touch it directly;
    /// callers only interact with the listener itself.
    /// </summary>
    private sealed class CStoreProvider : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        /// <summary>
        /// Set by <see cref="DicomScpListener.Start"/> before the server
        /// can accept any connection, and cleared by
        /// <see cref="DicomScpListener.Stop"/>. fo-dicom owns construction
        /// of this class (one instance per association) and has no way to
        /// inject the app's specific DicomFileParser/DicomProcessingService/
        /// AeTitle/logging into it, so every instance reaches back to
        /// whichever DicomScpListener is currently running through this
        /// static field instead. Safe because exactly one DicomScpListener
        /// runs per process (this step doesn't wire up more than one).
        /// </summary>
        public static DicomScpListener? ActiveListener;

        // Only the uncompressed transfer syntaxes: the same set
        // DicomFileParser (via fo-dicom + fo-dicom.Imaging.ImageSharp,
        // with no additional codec package referenced) already knows how
        // to render, so a study accepted here is guaranteed parseable by
        // the exact same pipeline the manual upload path uses.
        private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
        {
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian
        };

        public CStoreProvider(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
        }

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            DicomScpListener? listener = ActiveListener;

            if (listener is not null
                && !string.Equals(association.CalledAE, listener.AeTitle, StringComparison.OrdinalIgnoreCase))
            {
                listener._logError(
                    $"Rejected association from calling AE '{association.CallingAE}': " +
                    $"called AE Title '{association.CalledAE}' does not match this listener's AE Title '{listener.AeTitle}'.");
                return SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.CalledAENotRecognized);
            }

            foreach (DicomPresentationContext pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification
                    || pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                {
                    pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                }
                else
                {
                    pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                }
            }

            listener?._logInfo($"Association accepted from calling AE '{association.CallingAE}' ({association.RemoteHost}:{association.RemotePort}).");
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            return SendAssociationReleaseResponseAsync();
        }

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            ActiveListener?._logInfo("Received C-ECHO (verification) request.");
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            DicomScpListener? listener = ActiveListener;
            if (listener is null)
            {
                // Shouldn't happen — Start() sets ActiveListener before the
                // server can accept a connection — but never silently drop
                // an incoming image if it somehow does.
                return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.ProcessingFailure));
            }

            return Task.FromResult(listener.ProcessReceivedFile(request));
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        {
            ActiveListener?._logError($"Failed to parse an incoming C-STORE dataset: {e.Message}");
            return Task.CompletedTask;
        }

        /// <summary>Required by IDicomService (via IDicomServiceProvider). Nothing extra to do beyond logging — fo-dicom itself tears down the association.</summary>
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            ActiveListener?._logError($"Association aborted by {source}: {reason}.");
        }

        /// <summary>Required by IDicomService (via IDicomServiceProvider). Only worth logging when the connection closed because of an error rather than a normal release.</summary>
        public void OnConnectionClosed(Exception exception)
        {
            if (exception is not null)
            {
                ActiveListener?._logError($"Connection closed unexpectedly: {exception.Message}");
            }
        }
    }
}
