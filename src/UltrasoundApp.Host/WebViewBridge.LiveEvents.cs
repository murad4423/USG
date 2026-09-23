using System.Text.Json;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Dicom;

namespace UltrasoundApp.Host;

/// <summary>
/// Messages the host pushes to the page on its own (no request from React needed):
///   - "hostStatus"     — every few seconds: the bridge heartbeat plus whether the ultrasound machine saved
///                        in Settings is reachable. The navigation bar's indicators are driven by this.
///   - "studiesChanged" — the moment DicomScpListener has stored an image received from the machine, so the
///                        Patient List refreshes itself without a manual refresh.
/// Started once from MainForm (after Attach) via <see cref="StartLiveEvents"/>.
/// </summary>
public sealed partial class WebViewBridge
{
    private const string LogCategoryLive = "LIVE";
    private const int LiveStatusIntervalMs = 4000;

    private System.Windows.Forms.Timer? _liveStatusTimer;
    private bool _liveStatusCheckRunning;

    /// <summary>Starts the periodic "hostStatus" push and hooks the listener's "study received" event.</summary>
    public void StartLiveEvents()
    {
        if (_liveStatusTimer is not null)
        {
            return;
        }

        _dicomScpListener.StudyReceived += OnStudyReceivedFromListener;

        // As soon as the page has loaded, push a status right away instead of waiting for the next tick.
        _webView.CoreWebView2.NavigationCompleted += async (s, e) => await PublishHostStatusAsync();

        _liveStatusTimer = new System.Windows.Forms.Timer { Interval = LiveStatusIntervalMs };
        _liveStatusTimer.Tick += async (s, e) => await PublishHostStatusAsync();
        _liveStatusTimer.Start();
    }

    /// <summary>Called on a background (fo-dicom) thread whenever an incoming image was stored.</summary>
    private void OnStudyReceivedFromListener()
    {
        try
        {
            if (_webView.IsDisposed)
            {
                return;
            }

            _webView.BeginInvoke(new Action(() =>
            {
                try
                {
                    PostLiveMessage(new { type = "studiesChanged" });
                }
                catch (Exception ex)
                {
                    AppLog.Error(LogCategoryLive, "Could not send the 'studiesChanged' message to the page.", ex);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window is closing or its handle doesn't exist yet — nothing to refresh.
        }
    }

    /// <summary>Checks the saved machine (quick TCP connect) and pushes one "hostStatus" message. Always runs on the UI thread.</summary>
    private async Task PublishHostStatusAsync()
    {
        if (_liveStatusCheckRunning)
        {
            return;
        }

        _liveStatusCheckRunning = true;
        try
        {
            DicomMachineConfigDto config = LoadDicomMachineConfig();
            string host = (config.IpAddress ?? string.Empty).Trim();

            object machine;
            if (host.Length == 0)
            {
                s_lastMachineReachable = null;
                machine = new { configured = false };
            }
            else
            {
                DicomConnectionTestResult outcome =
                    await DicomConnectionTester.CheckReachableAsync(host, config.Port, TimeSpan.FromSeconds(2));

                // Log only when the machine goes on/offline, not on every push.
                if (s_lastMachineReachable != outcome.Connected)
                {
                    AppLog.Info(LogCategoryLive,
                        outcome.Connected
                            ? $"DICOM machine at {host}:{config.Port} is now reachable."
                            : $"DICOM machine at {host}:{config.Port} is not reachable: {outcome.Message}");
                    s_lastMachineReachable = outcome.Connected;
                }

                machine = new
                {
                    configured = true,
                    reachable = outcome.Connected,
                    message = outcome.Message,
                    hint = outcome.Hint,
                    elapsedMs = outcome.ElapsedMs,
                    config
                };
            }

            PostLiveMessage(new { type = "hostStatus", machine });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryLive, "Could not publish the live host status.", ex);
        }
        finally
        {
            _liveStatusCheckRunning = false;
        }
    }

    private void PostLiveMessage(object message)
    {
        if (_webView.IsDisposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));
    }
}
