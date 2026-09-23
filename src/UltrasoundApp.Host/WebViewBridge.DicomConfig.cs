using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Dicom;
using UltrasoundApp.Printing;

namespace UltrasoundApp.Host;

/// <summary>
/// Settings &gt; "DICOM Configuration": the ultrasound machine's connection
/// details, "Test Connection", and the "what to enter on the machine" info.
///
/// This PC's own AE Title / listening port are NOT handled here — they stay
/// in AppSettings and go through the existing "saveSettings" request (which
/// also restarts the DicomScpListener). This file only adds:
///
///   - "getDicomMachineConfig" / "saveDicomMachineConfig" — the machine's
///     AE Title, IP address and port (plus an optional label), kept in
///     <c>data/dicom-machine.json</c> (a plain file, so no database change);
///   - "testDicomConnection" — C-ECHO to the machine (see
///     <see cref="DicomConnectionTester"/>), using the values currently on
///     screen, so it works before anything is saved;
///   - "getDicomNetworkInfo" — this PC's IPv4 addresses and the receiver's
///     current state, for the "what to enter on the machine" table.
///
/// Each replies with one "{requestType}Result" message.
///   - "checkDicomMachineStatus" — the live indicator's light check (see RunDicomMachineStatusCheckAsync).
/// </summary>
public sealed partial class WebViewBridge
{
    private const string LogCategoryDicomConfig = "DICOM-CONFIG";
    private const int DicomMachineMaxNameLength = 60;

    private static readonly JsonSerializerOptions DicomConfigFileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Last result of the live status check — only used to log when the machine goes on/offline (not every poll).
    private static bool? s_lastMachineReachable;

    /// <summary>Entry point called from OnWebMessageReceived for the five request types above.</summary>
    private async void HandleDicomConfigRequest(string requestType, string rawMessage)
    {
        string resultType = requestType + "Result";

        try
        {
            DicomConfigRequest? request = JsonSerializer.Deserialize<DicomConfigRequest>(rawMessage);

            switch (requestType)
            {
                case "getDicomMachineConfig":
                    Send(new DicomConfigResult { Type = resultType, Success = true, Config = LoadDicomMachineConfig() });
                    break;

                case "saveDicomMachineConfig":
                    SaveDicomMachineConfig(resultType, request?.Config);
                    break;

                case "getDicomNetworkInfo":
                    SendDicomNetworkInfo(resultType);
                    break;

                case "checkDicomMachineStatus":
                    await RunDicomMachineStatusCheckAsync(resultType);
                    break;

                case "testDicomConnection":
                    // The continuation after this await resumes on the UI thread, where Send() must be called.
                    await RunDicomConnectionTestAsync(resultType, request?.Test);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryDicomConfig, $"DICOM configuration request '{requestType}' failed.", ex);
            Send(new DicomConfigResult
            {
                Type = resultType,
                Success = false,
                Error = "The DICOM configuration request failed. See data/logs/ for details."
            });
        }
    }

    private void SaveDicomMachineConfig(string resultType, DicomMachineConfigDto? incoming)
    {
        if (incoming is null)
        {
            SendDicomConfigError(resultType, "No machine settings were received.");
            return;
        }

        string name = (incoming.MachineName ?? string.Empty).Trim();
        string aeTitle = (incoming.AeTitle ?? string.Empty).Trim();
        string host = (incoming.IpAddress ?? string.Empty).Trim();
        int port = incoming.Port;

        if (name.Length > DicomMachineMaxNameLength)
        {
            SendDicomConfigError(resultType, $"The machine name is too long (max {DicomMachineMaxNameLength} characters).");
            return;
        }

        // The machine section is optional (receiving images doesn't need it) — leaving both AE Title and
        // IP blank just clears it. As soon as either is filled in, all three must be valid.
        bool blank = aeTitle.Length == 0 && host.Length == 0;
        if (!blank)
        {
            string? error =
                DicomConnectionTester.ValidateAeTitle(aeTitle, "Machine AE Title")
                ?? DicomConnectionTester.ValidateHost(host)
                ?? DicomConnectionTester.ValidatePort(port);
            if (error is not null)
            {
                SendDicomConfigError(resultType, error);
                return;
            }
        }

        var toSave = new DicomMachineConfigDto
        {
            MachineName = name,
            AeTitle = aeTitle,
            IpAddress = host,
            Port = blank && (port < 1 || port > 65535) ? DefaultDicomMachinePort : port
        };

        string path = GetDicomMachineConfigPath();
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(toSave, DicomConfigFileJsonOptions), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);

        AppLog.Info(LogCategoryDicomConfig,
            blank ? "Cleared the DICOM machine connection details." : $"Saved DICOM machine '{aeTitle}' at {host}:{port}.");

        Send(new DicomConfigResult { Type = resultType, Success = true, Config = toSave });
    }

    private async Task RunDicomConnectionTestAsync(string resultType, DicomTestRequestDto? test)
    {
        if (test is null)
        {
            SendDicomConfigError(resultType, "No connection details were received.");
            return;
        }

        DicomConnectionTestResult outcome = await DicomConnectionTester.TestAsync(
            test.CallingAeTitle ?? string.Empty,
            test.MachineAeTitle ?? string.Empty,
            test.IpAddress ?? string.Empty,
            test.Port);

        AppLog.Info(LogCategoryDicomConfig,
            $"Connection test to '{test.MachineAeTitle}' at {test.IpAddress}:{test.Port} -> {(outcome.Connected ? "OK" : "FAILED (" + outcome.Stage + ")")}: {outcome.Message}");

        Send(new DicomConfigResult
        {
            Type = resultType,
            Success = true,
            Connected = outcome.Connected,
            Stage = outcome.Stage,
            Message = outcome.Message,
            Hint = outcome.Hint,
            ElapsedMs = outcome.ElapsedMs,
            TcpReachable = outcome.TcpReachable
        });
    }

    /// <summary>
    /// Live status for the navigation bar: is the SAVED machine reachable right now? Polled every few seconds by
    /// the frontend, so it is deliberately light (one TCP connect, 2 s timeout) and only logs when the state changes.
    /// </summary>
    private async Task RunDicomMachineStatusCheckAsync(string resultType)
    {
        DicomMachineConfigDto config = LoadDicomMachineConfig();
        string host = (config.IpAddress ?? string.Empty).Trim();

        if (host.Length == 0)
        {
            s_lastMachineReachable = null;
            Send(new DicomConfigResult { Type = resultType, Success = true, Configured = false });
            return;
        }

        DicomConnectionTestResult outcome = await DicomConnectionTester.CheckReachableAsync(host, config.Port, TimeSpan.FromSeconds(2));

        if (s_lastMachineReachable != outcome.Connected)
        {
            AppLog.Info(LogCategoryDicomConfig,
                outcome.Connected
                    ? $"DICOM machine at {host}:{config.Port} is now reachable."
                    : $"DICOM machine at {host}:{config.Port} is not reachable: {outcome.Message}");
            s_lastMachineReachable = outcome.Connected;
        }

        Send(new DicomConfigResult
        {
            Type = resultType,
            Success = true,
            Configured = true,
            Reachable = outcome.Connected,
            Message = outcome.Message,
            Hint = outcome.Hint,
            ElapsedMs = outcome.ElapsedMs,
            Config = config
        });
    }

    private void SendDicomNetworkInfo(string resultType)
    {
        Send(new DicomConfigResult
        {
            Type = resultType,
            Success = true,
            HostName = Environment.MachineName,
            Addresses = GetLocalIPv4Addresses(),
            ListenerRunning = _dicomScpListener.IsRunning,
            ListenerAeTitle = _dicomScpListener.AeTitle,
            ListenerPort = _dicomScpListener.Port,
            ListenerError = _dicomScpListener.IsRunning ? null : _dicomScpListener.LastStartError
        });
    }

    private void SendDicomConfigError(string resultType, string error)
    {
        Send(new DicomConfigResult { Type = resultType, Success = false, Error = error });
    }

    private const int DefaultDicomMachinePort = 104;

    private static string GetDicomMachineConfigPath()
    {
        string dir = Path.Combine(PrintingPaths.GetRepoRoot(), "data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "dicom-machine.json");
    }

    private static DicomMachineConfigDto LoadDicomMachineConfig()
    {
        var defaults = new DicomMachineConfigDto
        {
            MachineName = string.Empty,
            AeTitle = string.Empty,
            IpAddress = string.Empty,
            Port = DefaultDicomMachinePort
        };

        try
        {
            string path = GetDicomMachineConfigPath();
            if (!File.Exists(path))
            {
                return defaults;
            }

            DicomMachineConfigDto? loaded = JsonSerializer.Deserialize<DicomMachineConfigDto>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded is null)
            {
                return defaults;
            }

            loaded.MachineName ??= string.Empty;
            loaded.AeTitle ??= string.Empty;
            loaded.IpAddress ??= string.Empty;
            if (loaded.Port < 1 || loaded.Port > 65535)
            {
                loaded.Port = DefaultDicomMachinePort;
            }

            return loaded;
        }
        catch (Exception ex)
        {
            // A damaged file must not break the Settings screen — start blank and let the operator re-enter it.
            AppLog.Error(LogCategoryDicomConfig, "Could not read data/dicom-machine.json; using blank machine settings.", ex);
            return defaults;
        }
    }

    /// <summary>
    /// This PC's usable IPv4 addresses — what the machine has to be told to send images to.
    /// Adapters with a default gateway (the real LAN) come first; loopback and link-local (169.254.x.x) are skipped.
    /// </summary>
    private static List<DicomNetworkAddressDto> GetLocalIPv4Addresses()
    {
        var addresses = new List<DicomNetworkAddressDto>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties = nic.GetIPProperties();
                bool hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    byte[] bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
                    {
                        continue;
                    }

                    addresses.Add(new DicomNetworkAddressDto
                    {
                        Name = nic.Name,
                        Address = unicast.Address.ToString(),
                        IsPrimary = hasGateway
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryDicomConfig, "Could not list this PC's network addresses.", ex);
        }

        return addresses
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Incoming request shape (only the fields these requests use).</summary>
    private sealed class DicomConfigRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("config")]
        public DicomMachineConfigDto? Config { get; init; }

        [JsonPropertyName("test")]
        public DicomTestRequestDto? Test { get; init; }
    }

    /// <summary>The machine's connection details — both the on-disk file format and the wire shape.</summary>
    private sealed class DicomMachineConfigDto
    {
        /// <summary>Optional label, e.g. "Samsung HS40 – Room 2". Not used for the connection.</summary>
        [JsonPropertyName("machineName")]
        public string? MachineName { get; set; }

        [JsonPropertyName("aeTitle")]
        public string? AeTitle { get; set; }

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }
    }

    private sealed class DicomTestRequestDto
    {
        /// <summary>This PC's AE Title (the caller, from the form — may not be saved yet).</summary>
        [JsonPropertyName("callingAeTitle")]
        public string? CallingAeTitle { get; set; }

        [JsonPropertyName("machineAeTitle")]
        public string? MachineAeTitle { get; set; }

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }
    }

    private sealed class DicomNetworkAddressDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        /// <summary>True for adapters that have a default gateway (the normal LAN connection).</summary>
        [JsonPropertyName("isPrimary")]
        public bool IsPrimary { get; set; }
    }

    /// <summary>Reply payload for all four DICOM configuration requests (only the fields relevant to the request are set).</summary>
    private sealed class DicomConfigResult
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        /// <summary>The request itself was handled (a failed connection test is still Success = true).</summary>
        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        // getDicomMachineConfig / saveDicomMachineConfig
        [JsonPropertyName("config")]
        public DicomMachineConfigDto? Config { get; init; }

        // checkDicomMachineStatus (also uses message / hint / elapsedMs / config)
        [JsonPropertyName("configured")]
        public bool? Configured { get; init; }

        [JsonPropertyName("reachable")]
        public bool? Reachable { get; init; }

        // testDicomConnection
        [JsonPropertyName("connected")]
        public bool? Connected { get; init; }

        [JsonPropertyName("stage")]
        public string? Stage { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("hint")]
        public string? Hint { get; init; }

        [JsonPropertyName("elapsedMs")]
        public long? ElapsedMs { get; init; }

        [JsonPropertyName("tcpReachable")]
        public bool? TcpReachable { get; init; }

        // getDicomNetworkInfo
        [JsonPropertyName("hostName")]
        public string? HostName { get; init; }

        [JsonPropertyName("addresses")]
        public List<DicomNetworkAddressDto>? Addresses { get; init; }

        [JsonPropertyName("listenerRunning")]
        public bool? ListenerRunning { get; init; }

        [JsonPropertyName("listenerAeTitle")]
        public string? ListenerAeTitle { get; init; }

        [JsonPropertyName("listenerPort")]
        public int? ListenerPort { get; init; }

        [JsonPropertyName("listenerError")]
        public string? ListenerError { get; init; }
    }
}
