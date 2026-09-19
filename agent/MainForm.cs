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
    private readonly Label _status = new();
    private readonly Label _detail = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private bool _requestedControl;
    private string? _sessionId;
    private string? _agentToken;
    private string? _webSocketUrl;
    private DateTime _liveExpiresAtUtc;
    private int _monitorIndex = -1;
    private int _jpegQuality = 55;
    private int _fps = 6;
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

        _status.SetBounds(36, 361, 455, 22);
        _status.Text = "Not connected";
        _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(100, 114, 130);

        _detail.SetBounds(36, 385, 455, 42);
        _detail.Text = $"Server: {_options.Server}";
        _detail.ForeColor = Color.FromArgb(120, 132, 145);
        _detail.Font = new Font("Segoe UI", 8F);

        panel.Controls.AddRange([brand, subtitle, intro, codeLabel, _code, _terms, _connect, _elevate, _disconnect, _status, _detail]);
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

            var permissions = lookup.RequestedControl
                ? "view your screen and control your keyboard and mouse"
                : "view your screen";
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
        _sessionCts = new CancellationTokenSource();

        SetStatus("Connecting to technician…");
        await OpenSocketAsync(_sessionCts.Token);
        StartTransferLoops(_sessionCts.Token);

        BeginInvoke((Action)(() =>
        {
            _disconnect.Visible = true;
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

        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            machine_name = Environment.MachineName,
            elevated = Program.IsAdministrator(),
            control = _requestedControl,
            monitors,
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            fps = Volatile.Read(ref _fps),
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

                var monitorIndex = Volatile.Read(ref _monitorIndex);
                var quality = Volatile.Read(ref _jpegQuality);
                var fps = Math.Clamp(Volatile.Read(ref _fps), 1, 12);

                var frame = ScreenCapture.CaptureJpeg(monitorIndex, quality);
                await SendBinaryAsync(frame, ct);
                await Task.Delay(Math.Max(1, 1000 / fps), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync("Screen stream interrupted.", ct);
        }
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
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested && !_explicitEndInProgress)
                _ = ScheduleReconnectAsync("Connection interrupted.", ct);
        }
    }

    private void ApplyCaptureSettings(JsonElement root)
    {
        if (root.TryGetProperty("monitor", out var monitorElement) && monitorElement.TryGetInt32(out var monitor))
            Volatile.Write(ref _monitorIndex, ScreenCapture.NormalizeScreenIndex(monitor));

        if (root.TryGetProperty("jpeg_quality", out var qualityElement) && qualityElement.TryGetInt32(out var quality))
            Volatile.Write(ref _jpegQuality, Math.Clamp(quality, 25, 85));

        if (root.TryGetProperty("fps", out var fpsElement) && fpsElement.TryGetInt32(out var fps))
            Volatile.Write(ref _fps, Math.Clamp(fps, 1, 12));

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)UpdateDetail);
    }

    private async Task SendCaptureSettingsAckAsync(CancellationToken ct)
    {
        var message = JsonSerializer.Serialize(new
        {
            type = "capture_settings",
            active_monitor = Volatile.Read(ref _monitorIndex),
            jpeg_quality = Volatile.Read(ref _jpegQuality),
            fps = Volatile.Read(ref _fps)
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
        Interlocked.Exchange(ref _reconnectGate, 0);

        if (updateUi && !IsDisposed && IsHandleCreated)
        {
            BeginInvoke((Action)(() =>
            {
                _disconnect.Visible = false;
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
        _detail.Text =
            $"Server: {_options.Server} · Monitor {Volatile.Read(ref _monitorIndex) + 1} · {Volatile.Read(ref _fps)} FPS · JPEG {Volatile.Read(ref _jpegQuality)}{expires}";
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
