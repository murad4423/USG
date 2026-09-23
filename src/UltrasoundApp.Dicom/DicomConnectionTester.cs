using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace UltrasoundApp.Dicom;

/// <summary>
/// Outcome of <see cref="DicomConnectionTester.TestAsync"/>.
/// <para><see cref="Stage"/> says how far the test got: "input" (bad values),
/// "network" (couldn't reach the machine's IP/port), "association" (reached
/// it, but it refused/aborted the DICOM connection), "echo" (connected, but
/// no usable C-ECHO answer) or "done" (everything worked).</para>
/// </summary>
public sealed record DicomConnectionTestResult(
    bool Connected,
    string Stage,
    string Message,
    string? Hint,
    long? ElapsedMs,
    bool TcpReachable);

/// <summary>
/// "Test connection" for the Settings screen: checks that this PC can reach
/// the ultrasound machine's DICOM port and that the machine answers a
/// DICOM C-ECHO (the standard "verification" ping).
///
/// Two steps, so a failure can be explained precisely instead of as one
/// generic "connection failed":
///   1. plain TCP connect to IP:port (wrong IP / cable / firewall / machine
///      off / wrong port all show up here);
///   2. a real DICOM association + C-ECHO using this PC's AE Title as the
///      calling AE and the machine's AE Title as the called AE (wrong AE
///      Title, "this PC isn't registered on the machine", no C-ECHO support
///      all show up here).
/// Never throws for an ordinary failure — every failure comes back as a
/// result with a plain-language message and a "what to check" hint.
/// </summary>
public static class DicomConnectionTester
{
    /// <summary>DICOM AE Titles are at most 16 characters.</summary>
    public const int MaxAeTitleLength = 16;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns an error message, or null if <paramref name="aeTitle"/> is a valid DICOM AE Title.</summary>
    public static string? ValidateAeTitle(string? aeTitle, string label)
    {
        string value = aeTitle?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            return $"{label} is required.";
        }

        if (value.Length > MaxAeTitleLength)
        {
            return $"{label} can be at most {MaxAeTitleLength} characters (DICOM rule).";
        }

        if (value.Any(c => c == '\\' || char.IsControl(c)))
        {
            return $"{label} must not contain backslashes or control characters.";
        }

        return null;
    }

    /// <summary>Returns an error message, or null if <paramref name="host"/> is a usable IP address or host name.</summary>
    public static string? ValidateHost(string? host)
    {
        string value = host?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            return "IP address is required.";
        }

        // "192.168.1" would otherwise be accepted by IPAddress.TryParse and silently read as 192.168.0.1.
        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            string[] parts = value.Split('.');
            bool ok = parts.Length == 4
                && parts.All(p => p.Length is > 0 and <= 3 && int.TryParse(p, out int octet) && octet <= 255);
            return ok ? null : "IP address must look like 192.168.1.50 (four numbers from 0 to 255).";
        }

        if (IPAddress.TryParse(value, out _))
        {
            return null;
        }

        return Uri.CheckHostName(value) == UriHostNameType.Unknown
            ? "That is not a valid IP address or host name."
            : null;
    }

    /// <summary>Returns an error message, or null if <paramref name="port"/> is a valid TCP port.</summary>
    public static string? ValidatePort(int port) =>
        port is >= 1 and <= 65535 ? null : "Port must be between 1 and 65535.";

    /// <summary>
    /// Quick "is the machine on and its DICOM port open?" check — just the TCP step of <see cref="TestAsync"/>,
    /// no DICOM association. Cheap enough to run every few seconds for the live status indicator; the full
    /// C-ECHO test stays behind the Test Connection button.
    /// </summary>
    public static async Task<DicomConnectionTestResult> CheckReachableAsync(
        string host,
        int port,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        host = host?.Trim() ?? string.Empty;

        string? inputError = ValidateHost(host) ?? ValidatePort(port);
        if (inputError is not null)
        {
            return new DicomConnectionTestResult(false, "input", inputError, null, null, false);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            DicomConnectionTestResult? failure =
                await CheckTcpAsync(host, port, timeout ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            return failure
                ?? new DicomConnectionTestResult(true, "done", $"The machine is reachable at {host}:{port}.", null, stopwatch.ElapsedMilliseconds, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DicomConnectionTestResult(false, "network", "The check was cancelled.", null, null, false);
        }
    }

    /// <param name="callingAeTitle">This PC's AE Title (what the machine will see as the caller).</param>
    /// <param name="machineAeTitle">The machine's AE Title (the called AE).</param>
    /// <param name="host">The machine's IP address (or host name).</param>
    /// <param name="port">The machine's DICOM port.</param>
    /// <param name="timeout">How long to wait for each step. Defaults to 5 seconds.</param>
    public static async Task<DicomConnectionTestResult> TestAsync(
        string callingAeTitle,
        string machineAeTitle,
        string host,
        int port,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        callingAeTitle = callingAeTitle?.Trim() ?? string.Empty;
        machineAeTitle = machineAeTitle?.Trim() ?? string.Empty;
        host = host?.Trim() ?? string.Empty;
        TimeSpan wait = timeout ?? DefaultTimeout;

        string? inputError =
            ValidateAeTitle(callingAeTitle, "This PC's AE Title")
            ?? ValidateAeTitle(machineAeTitle, "The machine's AE Title")
            ?? ValidateHost(host)
            ?? ValidatePort(port);
        if (inputError is not null)
        {
            return new DicomConnectionTestResult(false, "input", inputError, null, null, false);
        }

        try
        {
            DicomConnectionTestResult? tcpFailure = await CheckTcpAsync(host, port, wait, cancellationToken).ConfigureAwait(false);
            if (tcpFailure is not null)
            {
                return tcpFailure;
            }

            return await CheckEchoAsync(callingAeTitle, machineAeTitle, host, port, wait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DicomConnectionTestResult(false, "network", "The test was cancelled.", null, null, false);
        }
    }

    /// <summary>Step 1: can we open a TCP connection to host:port? Returns null when we can.</summary>
    private static async Task<DicomConnectionTestResult?> CheckTcpAsync(
        string host, int port, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(wait);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NetworkFailure(
                $"No reply from {host}:{port} within {wait.TotalSeconds:0} seconds.",
                "Check that the machine is switched on, the IP address is correct, the network cable/Wi-Fi is connected, " +
                "and that this PC and the machine are on the same network.");
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => NetworkFailure(
                    $"The machine at {host} refused the connection on port {port}.",
                    "The machine is reachable but nothing is listening on that port. Check the Port number in the machine's DICOM settings " +
                    "(common ports are 104 and 11112)."),

                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => NetworkFailure(
                    $"Could not find \"{host}\".",
                    "Check the spelling of the address, or use the machine's IP address instead of a name."),

                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.TimedOut => NetworkFailure(
                    $"The machine at {host} could not be reached.",
                    "Check that the machine is on, the IP address is correct, the cable/Wi-Fi is connected, " +
                    "and that this PC and the machine are on the same network."),

                _ => NetworkFailure($"Could not connect to {host}:{port} ({ex.Message}).", null)
            };
        }
    }

    /// <summary>Step 2: open a DICOM association and send a C-ECHO.</summary>
    private static async Task<DicomConnectionTestResult> CheckEchoAsync(
        string callingAeTitle, string machineAeTitle, string host, int port, TimeSpan wait, CancellationToken cancellationToken)
    {
        FoDicomBootstrapper.EnsureInitialized();

        var stopwatch = Stopwatch.StartNew();
        DicomStatus? status = null;

        try
        {
            IDicomClient client = DicomClientFactory.Create(host, port, false, callingAeTitle, machineAeTitle);
            client.ClientOptions.AssociationRequestTimeoutInMs = (int)wait.TotalMilliseconds;
            client.ClientOptions.MaximumNumberOfConsecutiveTimedOutAssociationRequests = 1;

            var request = new DicomCEchoRequest { OnResponseReceived = (_, response) => status = response.Status };
            await client.AddRequestAsync(request).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(wait + TimeSpan.FromSeconds(3));
            await client.SendAsync(cts.Token).ConfigureAwait(false);
        }
        catch (DicomAssociationRejectedException ex)
        {
            return RejectedFailure(ex, callingAeTitle, machineAeTitle);
        }
        catch (DicomAssociationAbortedException ex)
        {
            return new DicomConnectionTestResult(
                false, "association",
                $"The machine started a DICOM connection but then aborted it ({ex.AbortReason}).",
                "Check the AE Titles on both sides. Some machines abort when they don't recognise the calling AE Title — " +
                "register this PC (its AE Title, IP address and port) on the machine and test again.",
                null, true);
        }
        catch (DicomAssociationRequestTimedOutException)
        {
            return new DicomConnectionTestResult(
                false, "association",
                "The machine accepted the network connection but did not answer the DICOM association request in time.",
                "Check the Machine AE Title and Port. Make sure the port belongs to the machine's DICOM service, not another service.",
                null, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DicomConnectionTestResult(
                false, "association",
                "The machine did not complete the DICOM handshake in time.",
                "Check the Machine AE Title and Port, then try again.",
                null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DicomConnectionTestResult(
                false, "association",
                $"The DICOM connection failed: {ex.Message}",
                "Check the Machine AE Title, IP address and Port.",
                null, true);
        }

        stopwatch.Stop();

        if (status is null)
        {
            return new DicomConnectionTestResult(
                false, "echo",
                "The machine accepted the connection but did not answer the verification (C-ECHO) request.",
                "The network path is fine. Some machines don't support C-ECHO — you can still check the setup by sending an image from the machine to this PC.",
                stopwatch.ElapsedMilliseconds, true);
        }

        if (status.State != DicomState.Success)
        {
            return new DicomConnectionTestResult(
                false, "echo",
                $"The machine answered, but reported: {status.Description}.",
                "The machine may not support verification (C-ECHO). The network path is fine.",
                stopwatch.ElapsedMilliseconds, true);
        }

        return new DicomConnectionTestResult(
            true, "done",
            $"Connected — the machine \"{machineAeTitle}\" at {host}:{port} answered the DICOM verification (C-ECHO).",
            null,
            stopwatch.ElapsedMilliseconds, true);
    }

    private static DicomConnectionTestResult RejectedFailure(
        DicomAssociationRejectedException ex, string callingAeTitle, string machineAeTitle)
    {
        return ex.RejectReason switch
        {
            DicomRejectReason.CalledAENotRecognized => new DicomConnectionTestResult(
                false, "association",
                $"The machine rejected the connection: it does not recognise the Machine AE Title \"{machineAeTitle}\".",
                "Open the machine's DICOM settings and copy its own AE Title exactly (spelling and upper/lower case).",
                null, true),

            DicomRejectReason.CallingAENotRecognized => new DicomConnectionTestResult(
                false, "association",
                $"The machine rejected the connection: it does not recognise this PC's AE Title \"{callingAeTitle}\".",
                "On the machine, add this PC as a DICOM node/destination (AE Title, IP address and port from \"What to enter on the machine\" below), then test again.",
                null, true),

            _ => new DicomConnectionTestResult(
                false, "association",
                $"The machine rejected the DICOM connection ({ex.RejectReason}).",
                "Check the AE Titles on both sides, and that this PC is registered on the machine.",
                null, true)
        };
    }

    private static DicomConnectionTestResult NetworkFailure(string message, string? hint) =>
        new(false, "network", message, hint, null, false);
}
