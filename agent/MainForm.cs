using System.Diagnostics;
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
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private bool _requestedControl;

    public MainForm(StartupOptions options)
    {
        _options = options;
        Text = "InnAware Remote Support";
        Width = 560; Height = 450; MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Segoe UI", 10F);
        BuildUi();
        _code.Text = NormalizeCode(options.Code ?? "");
        FormClosing += (_, _) => StopSession();
    }

    private void BuildUi()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(36) };
        Controls.Add(panel);
        var brand = new Label { Text = "innaware", Font = new Font("Segoe UI", 26F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 49, 82), AutoSize = true, Top = 28, Left = 36 };
        var subtitle = new Label { Text = "REMOTE SUPPORT  ·  TECHFINITY", Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Color.FromArgb(36, 99, 235), AutoSize = true, Top = 73, Left = 39 };
        var intro = new Label { Text = "Enter the temporary support code provided by your technician.", AutoSize = false, Width = 455, Height = 42, Top = 112, Left = 36, ForeColor = Color.FromArgb(85, 101, 120) };
        var codeLabel = new Label { Text = "Support code", AutoSize = true, Top = 162, Left = 36, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        _code.SetBounds(36, 186, 455, 43); _code.Font = new Font("Consolas", 17F, FontStyle.Bold); _code.MaxLength = 12; _code.TextAlign = HorizontalAlignment.Center;
        _terms.SetBounds(36, 245, 455, 45); _terms.Text = "I agree to the remote-support terms and understand that I must approve the technician's access."; _terms.ForeColor = Color.FromArgb(70, 83, 100);
        _connect.SetBounds(36, 302, 220, 45); _connect.Text = "Continue"; StylePrimary(_connect); _connect.Click += async (_, _) => await BeginSessionAsync();
        _elevate.SetBounds(271, 302, 220, 45); _elevate.Text = Program.IsAdministrator() ? "Running as Administrator" : "Restart as Administrator"; _elevate.Enabled = !Program.IsAdministrator(); _elevate.Click += (_, _) => RestartElevated();
        _disconnect.SetBounds(36, 302, 455, 45); _disconnect.Text = "End Support Session"; _disconnect.BackColor = Color.FromArgb(183,55,55); _disconnect.ForeColor = Color.White; _disconnect.FlatStyle = FlatStyle.Flat; _disconnect.Visible = false; _disconnect.Click += (_, _) => StopSession();
        _status.SetBounds(36, 361, 455, 22); _status.Text = "Not connected"; _status.Font = new Font("Segoe UI", 9F, FontStyle.Bold); _status.ForeColor = Color.FromArgb(100, 114, 130);
        _detail.SetBounds(36, 385, 455, 30); _detail.Text = $"Server: {_options.Server}"; _detail.ForeColor = Color.FromArgb(120, 132, 145); _detail.Font = new Font("Segoe UI", 8F);
        panel.Controls.AddRange([brand, subtitle, intro, codeLabel, _code, _terms, _connect, _elevate, _disconnect, _status, _detail]);
    }

    private static void StylePrimary(Button b)
    {
        b.BackColor = Color.FromArgb(36, 99, 235); b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
    }

    private async Task BeginSessionAsync()
    {
        if (_ws is not null) return;
        var code = NormalizeCode(_code.Text);
        if (code.Length != 8) { SetStatus("Enter the 8-digit code provided by your technician.", true); return; }
        if (!_terms.Checked) { SetStatus("Please accept the remote-support terms before continuing.", true); return; }
        ToggleEntry(false); SetStatus("Checking support code…");
        try
        {
            var lookup = await PostAsync<LookupResponse>("/api/agent/lookup", new LookupRequest { Code = code }, CancellationToken.None);
            var permissions = lookup.RequestedControl ? "view your screen and control your keyboard and mouse" : "view your screen";
            var elevation = lookup.RequestedElevation ? "\n\nThe technician indicated that Administrator access may be needed. Windows will still require you to approve any UAC elevation prompt locally." : "";
            var label = string.IsNullOrWhiteSpace(lookup.CustomerLabel) ? "this computer" : lookup.CustomerLabel;
            var answer = MessageBox.Show(this,
                $"Technician: {lookup.TechnicianName}\nSupport for: {label}\n\nThe technician is requesting permission to {permissions}.{elevation}\n\nAllow this temporary support session?",
                "Approve Remote Support", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { SetStatus("Support request cancelled."); ToggleEntry(true); return; }

            if (lookup.RequestedElevation && !Program.IsAdministrator())
            {
                var elevate = MessageBox.Show(this, "Administrator access was requested. Restart InnAware Remote Support as Administrator now? You will see a normal Windows UAC prompt and must approve it locally.", "Administrator Access", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                if (elevate == DialogResult.Yes) { RestartElevated(code); return; }
            }

            var redeem = await PostAsync<RedeemResponse>("/api/agent/redeem", new RedeemRequest { Code = code, MachineName = Environment.MachineName, TermsAccepted = true }, CancellationToken.None);
            _requestedControl = lookup.RequestedControl;
            await ConnectWebSocketAsync(redeem.WebSocketUrl, redeem.AgentToken);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true); ToggleEntry(true);
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
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)) ?? throw new InvalidOperationException("Server returned an empty response.");
    }

    private async Task ConnectWebSocketAsync(string wsUrl, string agentToken)
    {
        _sessionCts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + agentToken);
        SetStatus("Connecting to technician…");
        await _ws.ConnectAsync(new Uri(wsUrl), _sessionCts.Token);
        var hello = JsonSerializer.Serialize(new { type = "hello", machine_name = Environment.MachineName, elevated = Program.IsAdministrator(), control = _requestedControl });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, _sessionCts.Token);
        BeginInvoke((Action)(() => { _disconnect.Visible = true; _connect.Visible = false; _elevate.Visible = false; _code.Enabled = false; _terms.Enabled = false; SetStatus("Connected — technician access is active."); }));
        _ = Task.Run(() => CaptureLoopAsync(_sessionCts.Token));
        _ = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var frame = ScreenCapture.CaptureJpeg();
                await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                await Task.Delay(180, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeDisconnect("Screen sharing stopped: " + ex.Message); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream(); WebSocketReceiveResult result;
                var segment = new ArraySegment<byte>(buffer);
                do
                {
                    result = await _ws.ReceiveAsync(segment, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { SafeDisconnect("Technician ended or disconnected from the session."); return; }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text || !_requestedControl) continue;
                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "input" && root.TryGetProperty("input", out var input)) InputInjector.Apply(input);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeDisconnect("Connection closed: " + ex.Message); }
    }

    private void StopSession()
    {
        var cts = Interlocked.Exchange(ref _sessionCts, null); cts?.Cancel(); cts?.Dispose();
        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws is not null) { try { ws.Abort(); ws.Dispose(); } catch { } }
        if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { _disconnect.Visible = false; _connect.Visible = true; _elevate.Visible = true; ToggleEntry(true); SetStatus("Support session ended."); }));
    }

    private void SafeDisconnect(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() => { StopSession(); SetStatus(message, true); }));
    }

    private void ToggleEntry(bool enabled) { _code.Enabled = enabled; _terms.Enabled = enabled; _connect.Enabled = enabled; _elevate.Enabled = enabled && !Program.IsAdministrator(); }
    private void SetStatus(string message, bool error = false) { if (InvokeRequired) { BeginInvoke((Action)(() => SetStatus(message, error))); return; } _status.Text = message; _status.ForeColor = error ? Color.FromArgb(183,55,55) : Color.FromArgb(43,111,78); }

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
        catch (System.ComponentModel.Win32Exception) { SetStatus("Administrator restart was cancelled.", true); ToggleEntry(true); }
        catch (Exception ex) { SetStatus("Could not restart as Administrator: " + ex.Message, true); ToggleEntry(true); }
    }

    private static string NormalizeCode(string value) => new(value.Where(char.IsDigit).Take(8).ToArray());
}
