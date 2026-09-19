using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace InnAwareSupport.Agent;

internal sealed class MainForm : Form
{
    private readonly StartupOptions _options;
    private readonly HttpClient _http = new();
    private readonly TextBox _code = new();
    private readonly CheckBox _terms = new();
    private readonly Button _connect = new();
    private readonly Button _elevate = new();
    private readonly Button _disconnect = new();
    private readonly Button _sendFile = new();
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private bool _requestedControl;
    private bool _requestedClipboard;
    private bool _requestedFileTransfer;
    private string? _sessionId;
    private string? _agentToken;
    private string? _webSocketUrl;
    private DateTime _liveExpiresAtUtc;
    private int _monitorIndex = -1;
    private int _jpegQuality = 55;
    private int _scalePercent = 100;
    private int _fps = 6;
    private int _adaptiveFpsEnabled;
    private long _adaptiveFpsLastAdjust;
    private string _captureMode = "auto";
    private H264CapabilityInfo? _h264Capability;
    private string _captureBackend = "initializing";
    private byte[]? _lastSentFrame;
    private long _captureWindowSent;
    private long _captureWindowSkipped;
    private long _captureWindowBytes;
    private long _captureWindowElapsedTicks;
    private long _captureWindowSamples;
    private long _captureTelemetryStamp;
    private int _viewerConnected;
    private int _reconnectGate;
    private bool _explicitEndInProgress;
    private bool _closing;

    public MainForm(StartupOptions options)
    {
        _options = options;
        Text = "InnAware Remote Support";
        Width = 560;
        Height = 450;
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Segoe UI", 10F);
        BuildUi();
        _code.Text = NormalizeCode(options.Code ?? "");
        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36) };
        Controls.Add(panel);

        var brand = new Label
        {
            Text = "innaware",
            Font = new Font("Segoe UI", 26F, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 49, 82),
            AutoSize = true,
            Top = 28,
            Left = 36
        };
        var subtitle = new Label
        {
            Text = "REMOTE SUPPORT  ·  TECHFINITY",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(36, 99, 235),
            AutoSize = true,
            Top = 73,
            Left = 39
        };
        var intro = new Label
        {
            Text = "Enter the temporary support code provided by your technician.",
            AutoSize = false,
            Width = 455,
            Height = 42,
            Top = 112,
            Left = 36,
            ForeColor = Color.FromArgb(85, 101, 120)
        };
        var codeLabel = new Label
        {
            Text = "Support code",
            AutoSize = true,
            Top = 162,
            Left = 36,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        _code.SetBounds(36, 186, 455, 43);
        _code.Font = new Font("Consolas", 17F, FontStyle.Bold);
        _code.MaxLength = 12;
        _code.TextAlign = HorizontalAlignment.Center;

        _terms.SetBounds(36, 245, 455, 45);
        _terms.Text = "I agree to the remote-support terms and understand that I must approve the technician's access.";
        _terms.ForeColor = Color.FromArgb(70, 83, 100);

        _connect.SetBounds(36, 302, 220, 45);
        _connect.Text = "Continue";
        StylePrimary(_connect);
        _connect.Click += async (_, _) => await BeginSessionAsync();

        _elevate.SetBounds(271, 302, 220, 45);
        _elevate.Text = Program.IsAdministrator() ? "Running as Administrator" : "Restart as Administrator";
        _elevate.Enabled = !Program.IsAdministrator();
        _elevate.Click += (_, _) => RestartElevated();

        _disconnect.SetBounds(36, 302, 455, 45);
        _disconnect.Text = "End Support Session";
        _disconnect.BackColor = Color.FromArgb(183, 55, 55);
        _disconnect.ForeColor = Color.White;
        _disconnect.FlatStyle = FlatStyle.Flat;
        _disconnect.Visible = false;
        _disconnect.Click += async (_, _) => await EndSessionFromCustomerAsync();

        _sendFile.SetBounds(271, 302, 220, 45);
        _sendFile.Text = "Send File to Technician";
        _sendFile.BackColor = Color.FromArgb(234, 240, 247);
        _sendFile.ForeColor = Color.FromArgb(20, 49, 82);
        _sendFile.FlatStyle = FlatStyle.Flat;
        _sendFile.FlatAppearance.BorderSize = 0;
        _sendFile.Visible = false;
        _sendFile.Click += async (_, _) => await SendFileToTechnicianAsync();

        _status.SetBounds(36, 361, 455, 22);
        _status.Text = "Not connected";
        _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(100, 114, 130);

        _detail.SetBounds(36, 385, 455, 42);
        _detail.Text = $"Server: {_options.Server}";
        _detail.ForeColor = Color.FromArgb(120, 132, 145);
        _detail.Font = new Font("Segoe UI", 8F);

        panel.Controls.AddRange([brand, subtitle, intro, codeLabel, _code, _terms, _connect, _elevate, _disconnect, _sendFile, _status, _detail]);
    }

    private static void StylePrimary(Button b)
    {
        b.BackColor = Color.FromArgb(36, 99, 235);
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
    }

    private async Task BeginSessionAsync()
    {
        if (_sessionCts is not null) return;

        var code = NormalizeCode(_code.Text);
        if (code.Length != 8)
        {
            SetStatus("Enter the 8-digit code provided by your technician.", true);
            return;
        }
        if (!_terms.Checked)
        {
            SetStatus("Please accept the remote-support terms before continuing.", true);
            return;
        }

        ToggleEntry(false);
        SetStatus("Checking support code…");

        try
        {
            var lookup = await PostAsync<LookupResponse>(
                "/api/agent/lookup",
                new LookupRequest { Code = code },
                CancellationToken.None);

            var requestedAccess = new List<string> { "view your screen" };
            if (lookup.RequestedControl)
                requestedAccess.Add("control your keyboard and mouse");
            if (lookup.RequestedClipboard)
                requestedAccess.Add("read and set text in your Windows clipboard");
            if (lookup.RequestedFileTransfer)
                requestedAccess.Add("send and receive files you explicitly approve (up to 25 MB each)");
            var permissions = string.Join(", ", requestedAccess);
            var elevation = lookup.RequestedElevation
                ? "\n\nThe technician indicated that Administrator access may be needed. Windows will still require you to approve any UAC elevation prompt locally."
                : "";
            var label = string.IsNullOrWhiteSpace(lookup.CustomerLabel) ? "this computer" : lookup.CustomerLabel;

            var answer = MessageBox.Show(
                this,
                $"Technician: {lookup.TechnicianName}\nSupport for: {label}\n\nThe technician is requesting permission to {permissions}.{elevation}\n\nAllow this temporary support session?",
                "Approve Remote Support",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                SetStatus("Support request cancelled.");
                ToggleEntry(true);
                return;
            }

            if (lookup.RequestedElevation && !Program.IsAdministrator())
            {
                var elevate = MessageBox.Show(
                    this,
                    "Administrator access was requested. Restart InnAware Remote Support as Administrator now? You will see a normal Windows UAC prompt and must approve it locally.",
                    "Administrator Access",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2);

                if (elevate == DialogResult.Yes)
                {
                    RestartElevated(code);
                    return;
                }
            }

            var redeem = await PostAsync<RedeemResponse>(
                "/api/agent/redeem",
                new RedeemRequest
                {
                    Code = code,
                    MachineName = Environment.MachineName,
                    TermsAccepted = true
                },
                CancellationToken.None);

            _requestedControl = lookup.RequestedControl;
            _requestedClipboard = lookup.RequestedClipboard;
            _requestedFileTransfer = lookup.RequestedFileTransfer;
            await ConnectWebSocketAsync(
                redeem.WebSocketUrl,
                redeem.AgentToken,
                redeem.SessionId,
                redeem.LiveExpiresAt);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            ToggleEntry(true);
        }
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(_options.Server + path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: cancellationToken);
            throw new InvalidOperationException(err?.Error ?? $"Server returned {(int)response.StatusCode}.");
        }

        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("Server returned an empty response.");
    }

    private async Task ConnectWebSocketAsync(string wsUrl, string agentToken, string sessionId, DateTime liveExpiresAt)
    {
        _sessionId = sessionId;
        _agentToken = agentToken;
        _webSocketUrl = wsUrl;
        _liveExpiresAtUtc = liveExpiresAt.ToUniversalTime();
        _monitorIndex = ScreenCapture.NormalizeScreenIndex(-1);
        _jpegQuality = 55;
        _fps = 6;
        _viewerConnected = 0;
        _sessionCts = new CancellationTokenSource();

        SetStatus("Connecting to technician…");
        await OpenSocketAsync(_sessionCts.Token);
        StartTransferLoops(_sessionCts.Token);

        BeginInvoke((Action)(() =>
        {
            _disconnect.Visible = true;
            _sendFile.Visible = _requestedFileTransfer;
            _disconnect.SetBounds(36, 302, _requestedFileTransfer ? 220 : 455, 45);
            _connect.Visible = false;
            _elevate.Visible = false;
            _code.Enabled = false;
            _terms.Enabled = false;
            SetStatus("Connected — technician access is active.");
            UpdateDetail();
        }));
    }

    private async Task OpenSocketAsync(CancellationToken ct)
    {
        var wsUrl = _webSocketUrl ?? throw new InvalidOperationException("Support session WebSocket URL is missing.");
        var token = _agentToken ?? throw new InvalidOperationException("Support session credential is missing.");

        CloseCurrentSocket();
        Volatile.Write(ref _viewerConnected, 0);

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);

        await ws.ConnectAsync(new Uri(wsUrl), ct);
        Interlocked.Exchange(ref _ws, ws);

        await SendHelloAsync(ct);
        await SendCaptureSettingsAckAsync(ct);
    }

    private void StartTransferLoops(CancellationToken ct)
    {
        _ = Task.Run(() => CaptureLoopAsync(ct), ct);
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
    }

    private async Task SendHelloAsync(CancellationToken ct)
    {
        var monitors = ScreenCapture.GetMonitors()
            .Select(m => new
            {
                index = m.Index,
                name = m.DeviceName,
                width = m.Width,
                height = m.Height,
                primary = m.Primary
            })
            .ToArray();

        _h264Capability ??= H264Capability.Probe();

        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            machine_name = Environment.MachineName,
            agent_version = AgentBuildInfo.Version,
            agent_build = AgentBuildInfo.InformationalVersion,
            elevated = Program.IsAdministrator(),
            control = _requestedControl,
            clipboard = _requestedClipboard,
            file_transfer = _requestedFileTransfer,
            monitors,
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            scale_percent = Volatile.Read(ref _scalePercent),
            fps = Volatile.Read(ref _fps),
            adaptive_fps = Volatile.Read(ref _adaptiveFpsEnabled) == 1,
            capture_mode = Volatile.Read(ref _captureMode),
            h264_hardware_available = _h264Capability.HardwareAvailable,
            h264_hardware_encoders = _h264Capability.HardwareEncoders,
            h264_probe_error = _h264Capability.Error,
            live_expires_at = _liveExpiresAtUtc
        });

        await SendTextAsync(hello, ct);
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open) return;

                if (Volatile.Read(ref _viewerConnected) == 0)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                var monitorIndex = Volatile.Read(ref _monitorIndex);
                var quality = Volatile.Read(ref _jpegQuality);
                var scalePercent = Volatile.Read(ref _scalePercent);
                var fps = Math.Clamp(Volatile.Read(ref _fps), 1, 12);
                var framePeriodMs = Math.Max(1, 1000 / fps);
                var captureMode = Volatile.Read(ref _captureMode);

                var started = Stopwatch.GetTimestamp();
                var hasFrame = ScreenCapture.TryCaptureJpeg(
                    monitorIndex,
                    quality,
                    framePeriodMs,
                    scalePercent,
                    captureMode == "gdi",
                    out var frame,
                    out var backend);
                var elapsedTicks = Stopwatch.GetTimestamp() - started;

                Volatile.Write(ref _captureBackend, backend);
                Interlocked.Add(ref _captureWindowElapsedTicks, elapsedTicks);
                Interlocked.Increment(ref _captureWindowSamples);

                if (!hasFrame || frame is null)
                {
                    Interlocked.Increment(ref _captureWindowSkipped);
                    await MaybeSendCaptureTelemetryAsync(ct);
                    if (backend == "capture unavailable")
                        await Task.Delay(Math.Min(500, framePeriodMs), ct);
                    continue;
                }

                var previousFrame = _lastSentFrame;
                if (previousFrame is not null && previousFrame.AsSpan().SequenceEqual(frame))
                {
                    Interlocked.Increment(ref _captureWindowSkipped);
                    await MaybeSendCaptureTelemetryAsync(ct);
                    var duplicateElapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    var duplicateDelayMs = framePeriodMs - (int)Math.Ceiling(duplicateElapsedMs);
                    if (duplicateDelayMs > 0)
                        await Task.Delay(duplicateDelayMs, ct);
                    continue;
                }

                _lastSentFrame = frame;
                await SendBinaryAsync(frame, ct);
                Interlocked.Increment(ref _captureWindowSent);
                Interlocked.Add(ref _captureWindowBytes, frame.Length);
                await MaybeSendCaptureTelemetryAsync(ct);

                var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                var remainingMs = framePeriodMs - (int)Math.Ceiling(elapsedMs);
                if (remainingMs > 0)
                    await Task.Delay(remainingMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ScreenCapture.ResetAcceleratedCapture();
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync("Screen stream interrupted.", ct);
        }
    }

    private async Task MaybeSendCaptureTelemetryAsync(CancellationToken ct)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _captureTelemetryStamp);
        if (previous != 0 && (now - previous) < Stopwatch.Frequency)
            return;
        if (Interlocked.CompareExchange(ref _captureTelemetryStamp, now, previous) != previous)
            return;

        var sent = Interlocked.Exchange(ref _captureWindowSent, 0);
        var skipped = Interlocked.Exchange(ref _captureWindowSkipped, 0);
        var bytes = Interlocked.Exchange(ref _captureWindowBytes, 0);
        var elapsedTicks = Interlocked.Exchange(ref _captureWindowElapsedTicks, 0);
        var samples = Interlocked.Exchange(ref _captureWindowSamples, 0);
        var avgMs = samples > 0
            ? elapsedTicks * 1000.0 / Stopwatch.Frequency / samples
            : 0.0;

        var message = JsonSerializer.Serialize(new
        {
            type = "capture_telemetry",
            backend = Volatile.Read(ref _captureBackend),
            frames_sent = sent,
            frames_skipped = skipped,
            jpeg_bytes = bytes,
            average_capture_ms = Math.Round(avgMs, 2)
        });

        await SendTextAsync(message, ct);

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)UpdateDetail);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open) return;

                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                var segment = new ArraySegment<byte>(buffer);

                do
                {
                    result = await ws.ReceiveAsync(segment, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = result.CloseStatusDescription ?? "";
                        if (result.CloseStatus == WebSocketCloseStatus.NormalClosure &&
                            reason.Contains("session ended", StringComparison.OrdinalIgnoreCase))
                        {
                            SafeServerEnded("Support session ended by the technician or server.");
                        }
                        else if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                        {
                            _ = ScheduleReconnectAsync("Connection interrupted.", ct);
                        }
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeElement)) continue;

                var type = typeElement.GetString();

                if (type == "input" && _requestedControl && root.TryGetProperty("input", out var input))
                {
                    InputInjector.Apply(input, Volatile.Read(ref _monitorIndex));
                }
                else if (type == "capture_settings")
                {
                    ApplyCaptureSettings(root);
                    await SendCaptureSettingsAckAsync(ct);
                }
                else if (type == "viewer_status" &&
                         root.TryGetProperty("connected", out var connectedElement) &&
                         (connectedElement.ValueKind == JsonValueKind.True || connectedElement.ValueKind == JsonValueKind.False))
                {
                    ApplyViewerStatus(connectedElement.GetBoolean());
                }
                else if (type == "viewer_telemetry")
                {
                    if (ApplyViewerTelemetry(root))
                        await SendCaptureSettingsAckAsync(ct);
                }
                else if (type == "clipboard_set" && _requestedClipboard &&
                         root.TryGetProperty("text", out var clipboardTextElement))
                {
                    var text = clipboardTextElement.GetString() ?? "";
                    if (Encoding.UTF8.GetByteCount(text) <= 262144)
                    {
                        await SetClipboardTextAsync(text);
                        await SendTextAsync(JsonSerializer.Serialize(new
                        {
                            type = "clipboard_status",
                            action = "set",
                            ok = true,
                            length = text.Length
                        }), ct);
                    }
                }
                else if (type == "clipboard_get" && _requestedClipboard)
                {
                    var text = ClampUtf8Text(await GetClipboardTextAsync(), 262144);
                    await SendTextAsync(JsonSerializer.Serialize(new
                    {
                        type = "clipboard_data",
                        text,
                        length = text.Length
                    }), ct);
                }
                else if (type == "file_offer" && _requestedFileTransfer)
                {
                    await HandleIncomingFileOfferAsync(root, ct);
                }
                else if (type == "file_status" && _requestedFileTransfer)
                {
                    var name = root.TryGetProperty("name", out var fileNameElement)
                        ? Path.GetFileName(fileNameElement.GetString() ?? "file")
                        : "file";
                    var status = root.TryGetProperty("status", out var fileStatusElement)
                        ? fileStatusElement.GetString() ?? "updated"
                        : "updated";
                    SetStatus(status switch
                    {
                        "available_to_technician" => $"Technician received the offer for {name}.",
                        "technician_download_started" => $"Technician started downloading {name}.",
                        _ => $"{name}: {status}"
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync("Connection interrupted.", ct);
        }
    }

    private static string ClampUtf8Text(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= maxBytes)
                low = mid;
            else
                high = mid - 1;
        }

        if (low > 0 && low < text.Length &&
            char.IsHighSurrogate(text[low - 1]) && char.IsLowSurrogate(text[low]))
            low--;

        return text[..low];
    }

    private async Task SendFileToTechnicianAsync()
    {
        if (!_requestedFileTransfer || string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_agentToken))
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "Choose a file to send to your technician",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 25L * 1024 * 1024)
        {
            SetStatus("That file exceeds the 25 MB transfer limit.", true);
            return;
        }

        _sendFile.Enabled = false;
        SetStatus($"Uploading {info.Name} to technician…");

        try
        {
            using var file = File.OpenRead(info.FullName);
            using var content = new MultipartFormDataContent();
            using var stream = new StreamContent(file);
            stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(stream, "file", info.Name);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                _options.Server + "/api/agent/files?session=" + Uri.EscapeDataString(_sessionId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _agentToken);
            request.Content = content;

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<ErrorResponse>();
                throw new InvalidOperationException(err?.Error ?? $"Upload failed ({(int)response.StatusCode}).");
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var viewerNotified = false;
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    using var responseJson = JsonDocument.Parse(responseText);
                    viewerNotified =
                        responseJson.RootElement.TryGetProperty("viewer_notified", out var notifiedElement) &&
                        notifiedElement.ValueKind == JsonValueKind.True;
                }
                catch { }
            }

            SetStatus(viewerNotified
                ? $"Sent {info.Name} to technician."
                : $"Uploaded {info.Name}. It will appear when the technician viewer is connected.");
        }
        catch (Exception ex)
        {
            SetStatus("File transfer failed: " + ex.Message, true);
        }
        finally
        {
            _sendFile.Enabled = true;
        }
    }

    private async Task HandleIncomingFileOfferAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("transfer_id", out var idElement)) return;
        var transferId = idElement.GetString() ?? "";
        if (transferId == "") return;

        var name = root.TryGetProperty("name", out var nameElement)
            ? Path.GetFileName(nameElement.GetString() ?? "support-file.bin")
            : "support-file.bin";
        var size = root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
            ? parsedSize
            : 0L;

        var savePath = await PromptForIncomingFileAsync(name, size);
        if (savePath is null)
        {
            await SendFileStatusAsync(transferId, name, "declined", ct);
            return;
        }

        try
        {
            SetStatus($"Downloading {name}…");

            var sessionId = _sessionId ?? throw new InvalidOperationException("Session is unavailable.");
            var token = _agentToken ?? throw new InvalidOperationException("Session credential is unavailable.");
            var url = _options.Server + "/api/agent/files/" + Uri.EscapeDataString(transferId) +
                      "?session=" + Uri.EscapeDataString(sessionId);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var tempPath = savePath + ".innaware-part-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                {
                    await input.CopyToAsync(output, ct);
                }

                File.Move(tempPath, savePath, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            await SendFileStatusAsync(transferId, name, "saved", ct);
            SetStatus($"Saved {name}.");
        }
        catch (Exception ex)
        {
            try { await SendFileStatusAsync(transferId, name, "failed", ct); } catch { }
            SetStatus("File download failed: " + ex.Message, true);
        }
    }

    private Task<string?> PromptForIncomingFileAsync(string name, long size)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            var sizeText = size >= 1024 * 1024
                ? $"{size / (1024d * 1024d):0.00} MB"
                : $"{Math.Max(0, size) / 1024d:0.0} KB";

            var answer = MessageBox.Show(
                this,
                $"Your technician wants to send this file:\n\n{name}\n{sizeText}\n\nChoose Yes to select where to save it.",
                "Incoming Support File",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                tcs.SetResult(null);
                return;
            }

            using var save = new SaveFileDialog
            {
                Title = "Save support file",
                FileName = name,
                OverwritePrompt = true
            };
            tcs.SetResult(save.ShowDialog(this) == DialogResult.OK ? save.FileName : null);
        }));

        return tcs.Task;
    }

    private Task SendFileStatusAsync(string transferId, string name, string status, CancellationToken ct)
    {
        return SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "file_status",
            transfer_id = transferId,
            name,
            status
        }), ct);
    }

    private Task<string> GetClipboardTextAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult("");
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            try
            {
                var text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : "";
                tcs.SetResult(text);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));

        return tcs.Task;
    }

    private Task SetClipboardTextAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated)
        {
            tcs.SetResult(true);
            return tcs.Task;
        }

        BeginInvoke((Action)(() =>
        {
            try
            {
                Clipboard.SetDataObject(text, true, 5, 100);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));

        return tcs.Task;
    }

    private void ApplyViewerStatus(bool connected)
    {
        Volatile.Write(ref _viewerConnected, connected ? 1 : 0);
        if (connected)
            _lastSentFrame = null;

        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (_sessionCts is null) return;
            SetStatus(
                connected
                    ? "Connected — technician viewer is active."
                    : "Connected — waiting for technician viewer.");
            UpdateDetail();
        }));
    }

    private void ApplyCaptureSettings(JsonElement root)
    {
        if (root.TryGetProperty("monitor", out var monitorElement) && monitorElement.TryGetInt32(out var monitor))
        {
            var normalized = ScreenCapture.NormalizeScreenIndex(monitor);
            var previous = Volatile.Read(ref _monitorIndex);
            Volatile.Write(ref _monitorIndex, normalized);
            if (normalized != previous)
            {
                _lastSentFrame = null;
                ScreenCapture.ResetAcceleratedCapture();
            }
        }

        if (root.TryGetProperty("jpeg_quality", out var qualityElement) && qualityElement.TryGetInt32(out var quality))
        {
            var nextQuality = Math.Clamp(quality, 25, 85);
            if (nextQuality != Volatile.Read(ref _jpegQuality))
                _lastSentFrame = null;
            Volatile.Write(ref _jpegQuality, nextQuality);
        }

        if (root.TryGetProperty("scale_percent", out var scaleElement) && scaleElement.TryGetInt32(out var scalePercent))
        {
            var nextScale = scalePercent switch
            {
                <= 50 => 50,
                <= 75 => 75,
                _ => 100
            };
            if (nextScale != Volatile.Read(ref _scalePercent))
                _lastSentFrame = null;
            Volatile.Write(ref _scalePercent, nextScale);
        }

        var adaptive = root.TryGetProperty("adaptive_fps", out var adaptiveElement) &&
                       adaptiveElement.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("fps", out var fpsElement) && fpsElement.TryGetInt32(out var fps))
        {
            if (adaptive || fps == 0)
            {
                Volatile.Write(ref _adaptiveFpsEnabled, 1);
                var current = Volatile.Read(ref _fps);
                if (current < 2 || current > 12)
                    Volatile.Write(ref _fps, 8);
                Interlocked.Exchange(ref _adaptiveFpsLastAdjust, 0);
            }
            else
            {
                Volatile.Write(ref _adaptiveFpsEnabled, 0);
                Volatile.Write(ref _fps, Math.Clamp(fps, 1, 12));
            }
        }

        if (root.TryGetProperty("capture_mode", out var modeElement))
        {
            var nextMode = string.Equals(modeElement.GetString(), "gdi", StringComparison.OrdinalIgnoreCase)
                ? "gdi"
                : "auto";
            if (!string.Equals(nextMode, Volatile.Read(ref _captureMode), StringComparison.Ordinal))
            {
                Volatile.Write(ref _captureMode, nextMode);
                _lastSentFrame = null;
                ScreenCapture.ResetAcceleratedCapture();
            }
        }

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)UpdateDetail);
    }

    private bool ApplyViewerTelemetry(JsonElement root)
    {
        if (Volatile.Read(ref _adaptiveFpsEnabled) != 1)
            return false;

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _adaptiveFpsLastAdjust);
        if (last != 0 && now - last < Stopwatch.Frequency * 2)
            return false;

        var dropped = root.TryGetProperty("dropped", out var droppedElement) &&
                      droppedElement.TryGetInt32(out var droppedValue)
            ? Math.Max(0, droppedValue)
            : 0;
        var renderedFps = root.TryGetProperty("rendered_fps", out var renderedElement) &&
                          renderedElement.TryGetDouble(out var renderedValue)
            ? Math.Max(0, renderedValue)
            : 0;
        var averageDecodeMs = root.TryGetProperty("average_decode_ms", out var decodeElement) &&
                              decodeElement.TryGetDouble(out var decodeValue)
            ? Math.Max(0, decodeValue)
            : 0;

        var current = Math.Clamp(Volatile.Read(ref _fps), 2, 12);
        var next = current;
        var budgetMs = 1000.0 / current;

        if (dropped >= 2 || averageDecodeMs > budgetMs * 0.80)
        {
            next = Math.Max(2, current - 2);
        }
        else if (dropped == 0 &&
                 renderedFps >= current * 0.80 &&
                 averageDecodeMs > 0 &&
                 averageDecodeMs < budgetMs * 0.45)
        {
            next = Math.Min(12, current + 1);
        }

        if (next == current)
            return false;

        Volatile.Write(ref _fps, next);
        Interlocked.Exchange(ref _adaptiveFpsLastAdjust, now);
        return true;
    }

    private async Task SendCaptureSettingsAckAsync(CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "capture_settings",
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            scale_percent = Volatile.Read(ref _scalePercent),
            fps = Volatile.Read(ref _fps),
            adaptive_fps = Volatile.Read(ref _adaptiveFpsEnabled) == 1,
            capture_mode = Volatile.Read(ref _captureMode)
        });
        await SendTextAsync(message, ct);
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
                throw new WebSocketException("WebSocket is not open.");

            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var ws = _ws;
            if (ws is null || ws.State != WebSocketState.Open)
                throw new WebSocketException("WebSocket is not open.");

            await ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ScheduleReconnectAsync(string reason, CancellationToken sessionToken)
    {
        if (sessionToken.IsCancellationRequested || _explicitEndInProgress) return;
        if (Interlocked.CompareExchange(ref _reconnectGate, 1, 0) != 0) return;

        try
        {
            CloseCurrentSocket();

            for (var attempt = 1; attempt <= 5 && !sessionToken.IsCancellationRequested; attempt++)
            {
                if (_liveExpiresAtUtc != default && DateTime.UtcNow >= _liveExpiresAtUtc)
                {
                    SafeServerEnded("Support session expired.");
                    return;
                }

                SetStatus($"{reason} Reconnecting ({attempt}/5)…", true);

                var delaySeconds = attempt switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 4,
                    4 => 8,
                    _ => 10
                };

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), sessionToken);
                    await OpenSocketAsync(sessionToken);
                    StartTransferLoops(sessionToken);
                    SetStatus("Connected — technician access is active.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    CloseCurrentSocket();
                }
            }

            SafeServerEnded("Connection lost. The support session could not be reconnected.", true);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectGate, 0);
        }
    }

    private async Task EndSessionFromCustomerAsync()
    {
        if (_explicitEndInProgress) return;
        _explicitEndInProgress = true;
        _disconnect.Enabled = false;
        SetStatus("Ending support session…");

        var revoked = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            revoked = await NotifyServerEndAsync(cts.Token);
        }
        catch { }

        StopLocalSession();

        if (!IsDisposed)
        {
            _disconnect.Enabled = true;
            SetStatus(
                revoked ? "Support session ended." : "Disconnected locally — server revocation could not be confirmed.",
                !revoked);
        }

        _explicitEndInProgress = false;
    }

    private async Task<bool> NotifyServerEndAsync(CancellationToken ct)
    {
        var sessionId = _sessionId;
        var token = _agentToken;

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(token))
            return true;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Server + "/api/agent/end");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new EndSessionRequest { SessionId = sessionId });

        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict;
    }

    private void TryNotifyEndSync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_agentToken)) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            _ = NotifyServerEndAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;

        if (!_explicitEndInProgress)
            TryNotifyEndSync();

        StopLocalSession(updateUi: false);
    }

    private void StopLocalSession(bool updateUi = true)
    {
        var cts = Interlocked.Exchange(ref _sessionCts, null);
        cts?.Cancel();
        cts?.Dispose();

        CloseCurrentSocket();

        _sessionId = null;
        _agentToken = null;
        _webSocketUrl = null;
        _liveExpiresAtUtc = default;
        _lastSentFrame = null;
        ScreenCapture.ResetAcceleratedCapture();
        Volatile.Write(ref _scalePercent, 100);
        Volatile.Write(ref _adaptiveFpsEnabled, 0);
        Interlocked.Exchange(ref _adaptiveFpsLastAdjust, 0);
        Volatile.Write(ref _captureMode, "auto");
        Volatile.Write(ref _captureBackend, "initializing");
        Interlocked.Exchange(ref _captureTelemetryStamp, 0);
        Interlocked.Exchange(ref _captureWindowSent, 0);
        Interlocked.Exchange(ref _captureWindowSkipped, 0);
        Interlocked.Exchange(ref _captureWindowBytes, 0);
        Interlocked.Exchange(ref _captureWindowElapsedTicks, 0);
        Interlocked.Exchange(ref _captureWindowSamples, 0);
        Volatile.Write(ref _viewerConnected, 0);
        Interlocked.Exchange(ref _reconnectGate, 0);

        if (updateUi && !IsDisposed && IsHandleCreated)
        {
            BeginInvoke((Action)(() =>
            {
                _disconnect.Visible = false;
                _sendFile.Visible = false;
                _disconnect.SetBounds(36, 302, 455, 45);
                _connect.Visible = true;
                _elevate.Visible = true;
                _code.Enabled = true;
                _terms.Enabled = true;
                ToggleEntry(true);
                _detail.Text = $"Server: {_options.Server}";
            }));
        }
    }

    private void CloseCurrentSocket()
    {
        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws is null) return;

        try { ws.Abort(); } catch { }
        try { ws.Dispose(); } catch { }
    }

    private void SafeServerEnded(string message, bool error = false)
    {
        if (IsDisposed || !IsHandleCreated) return;

        BeginInvoke((Action)(() =>
        {
            StopLocalSession();
            SetStatus(message, error);
        }));
    }

    private void UpdateDetail()
    {
        var expires = _liveExpiresAtUtc == default
            ? ""
            : $" · Session expires {_liveExpiresAtUtc.ToLocalTime():g}";
        var viewer = Volatile.Read(ref _viewerConnected) == 1 ? "Viewer attached" : "Waiting for viewer";
        var backend = Volatile.Read(ref _captureBackend);
        _detail.Text =
            $"Server: {_options.Server} · {backend} · Monitor {Volatile.Read(ref _monitorIndex) + 1} · {Volatile.Read(ref _scalePercent)}% · {Volatile.Read(ref _fps)} FPS · JPEG {Volatile.Read(ref _jpegQuality)} · {viewer}{expires}";
    }

    private void ToggleEntry(bool enabled)
    {
        _code.Enabled = enabled;
        _terms.Enabled = enabled;
        _connect.Enabled = enabled;
        _elevate.Enabled = enabled && !Program.IsAdministrator();
    }

    private void SetStatus(string message, bool error = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetStatus(message, error)));
            return;
        }

        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(183, 55, 55) : Color.FromArgb(43, 111, 78);
    }

    private void RestartElevated(string? code = null)
    {
        if (Program.IsAdministrator()) return;

        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var effectiveCode = NormalizeCode(code ?? _code.Text);
            var args = $"--server \"{_options.Server}\"" + (effectiveCode.Length == 8 ? $" --code {effectiveCode}" : "");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            SetStatus("Administrator restart was cancelled.", true);
            ToggleEntry(true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not restart as Administrator: " + ex.Message, true);
            ToggleEntry(true);
        }
    }

    private static string NormalizeCode(string value) => new(value.Where(char.IsDigit).Take(8).ToArray());
}
