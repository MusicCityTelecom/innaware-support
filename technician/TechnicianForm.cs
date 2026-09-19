using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace InnAwareSupport.Technician;

internal sealed class TechnicianForm : Form
{
    private readonly string _server;
    private readonly string _startUrl;
    private readonly WebView2 _web = new();
    private readonly ToolStrip _tools = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new();
    private readonly ToolStripComboBox _monitor = new();
    private readonly ToolStripComboBox _capture = new();
    private readonly ToolStripComboBox _resolution = new();
    private readonly ToolStripComboBox _quality = new();
    private readonly ToolStripComboBox _fps = new();
    private readonly System.Windows.Forms.Timer _viewerSyncTimer = new();
    private readonly bool _viewerWindow;

    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private bool _nativeFullscreen;

    public TechnicianForm(string server, string? startUrl = null)
    {
        _server = server.TrimEnd('/');
        _startUrl = string.IsNullOrWhiteSpace(startUrl)
            ? _server
            : startUrl;
        _viewerWindow =
            Uri.TryCreate(_startUrl, UriKind.Absolute, out var startUri) &&
            startUri.Query.Contains("popout=1", StringComparison.OrdinalIgnoreCase);

        Text = _viewerWindow
            ? "InnAware Support — Detached Viewer"
            : "InnAware Support Technician";
        Width = 1500;
        Height = 950;
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();

        _web.Dock = DockStyle.Fill;
        _statusText.Text = "Starting technician console…";
        _status.Items.Add(_statusText);

        Controls.Add(_web);
        Controls.Add(_tools);
        Controls.Add(_status);

        _tools.Dock = DockStyle.Top;
        _status.Dock = DockStyle.Bottom;

        Shown += async (_, _) => await InitializeBrowserAsync();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F11) return;
            e.SuppressKeyPress = true;
            ToggleNativeFullscreen();
        };

        _viewerSyncTimer.Interval = 1500;
        _viewerSyncTimer.Tick += async (_, _) =>
            await SyncViewerControlsAsync();
        FormClosed += (_, _) => _viewerSyncTimer.Dispose();
    }

    private void BuildToolbar()
    {
        _tools.GripStyle = ToolStripGripStyle.Hidden;
        _tools.Padding = new Padding(6, 4, 6, 4);
        _tools.AutoSize = true;

        if (!_viewerWindow)
        {
            AddButton("Home", (_, _) => Navigate(_server));
            AddButton("Back", (_, _) =>
            {
                if (_web.CanGoBack) _web.GoBack();
            });
            AddButton("Forward", (_, _) =>
            {
                if (_web.CanGoForward) _web.GoForward();
            });
            AddButton("Refresh", (_, _) => _web.Reload());
            _tools.Items.Add(new ToolStripSeparator());
            AddButton("Detach", async (_, _) =>
                await ClickWebButtonAsync("popoutViewerButton"));
        }
        else
        {
            AddButton("Close Viewer", (_, _) => Close());
        }

        AddButton("Fit", async (_, _) =>
            await ClickWebButtonAsync("fitViewButton"));
        AddButton("1:1", async (_, _) =>
            await ClickWebButtonAsync("actualViewButton"));

        _tools.Items.Add(new ToolStripSeparator());

        ConfigureCombo(
            _monitor,
            "Monitor",
            ["Monitor 1"],
            async value => await SetWebSelectAsync("monitorSelect", MonitorValue(value)));
        _monitor.DropDown += async (_, _) => await RefreshMonitorChoicesAsync();

        ConfigureCombo(
            _capture,
            "Capture",
            ["Auto (DXGI)", "Compatibility (GDI)"],
            async value => await SetWebSelectAsync(
                "captureModeSelect",
                value.StartsWith("Compatibility", StringComparison.Ordinal) ? "gdi" : "auto"));

        ConfigureCombo(
            _resolution,
            "Resolution",
            ["100%", "75%", "50%"],
            async value => await SetWebSelectAsync(
                "scaleSelect",
                value.TrimEnd('%')));

        ConfigureCombo(
            _quality,
            "Quality",
            ["Low", "Balanced", "High", "Very high"],
            async value => await SetWebSelectAsync(
                "qualitySelect",
                value switch
                {
                    "Low" => "35",
                    "High" => "70",
                    "Very high" => "85",
                    _ => "55"
                }));

        ConfigureCombo(
            _fps,
            "FPS",
            ["Adaptive", "2 FPS", "4 FPS", "6 FPS", "8 FPS", "10 FPS", "12 FPS"],
            async value => await SetWebSelectAsync(
                "fpsSelect",
                value == "Adaptive"
                    ? "0"
                    : new string(value.TakeWhile(char.IsDigit).ToArray())));

        _tools.Items.Add(new ToolStripSeparator());

        AddButton("Record", async (_, _) =>
            await ClickWebButtonAsync("recordViewerButton"));
        AddButton("Chat", async (_, _) =>
            await FocusWebElementAsync("chatBody"));
        AddButton("Files", async (_, _) =>
            await ScrollWebElementAsync("fileTransferCard"));
        AddButton("Elevate", async (_, _) =>
            await ClickWebButtonAsync("requestElevationButton"));

        _tools.Items.Add(new ToolStripSeparator());

        AddButton("Native Fullscreen", (_, _) => ToggleNativeFullscreen());

        var topMost = new ToolStripButton("Always on top")
        {
            CheckOnClick = true
        };
        topMost.CheckedChanged += (_, _) => TopMost = topMost.Checked;
        _tools.Items.Add(topMost);
    }

    private void ConfigureCombo(
        ToolStripComboBox combo,
        string label,
        string[] items,
        Func<string, Task> changed)
    {
        _tools.Items.Add(new ToolStripLabel(label));
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.AutoSize = false;
        combo.Width = label == "Capture" ? 138 : 92;
        combo.Items.AddRange(items);
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        combo.SelectionChangeCommitted += async (_, _) =>
        {
            if (combo.SelectedItem is string value)
                await changed(value);
        };
        _tools.Items.Add(combo);
    }

    private void AddButton(string text, EventHandler onClick)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true
        };
        button.Click += onClick;
        _tools.Items.Add(button);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var environment = await BrowserRuntime.GetAsync();
            await _web.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += async (_, e) =>
            {
                _statusText.Text = e.IsSuccess
                    ? "Connected to InnAware Support"
                    : $"Navigation error: {e.WebErrorStatus}";

                if (e.IsSuccess)
                {
                    _viewerSyncTimer.Start();
                    await SyncViewerControlsAsync();
                }
            };
            core.SourceChanged += (_, _) =>
            {
                _statusText.Text = core.Source;
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                var title = core.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(title)
                    ? "InnAware Support Technician"
                    : $"{title} — Technician";
            };
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;

            Navigate(_startUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime is required for InnAware Support Technician. Install the WebView2 Evergreen Runtime and start the application again.",
                "WebView2 Runtime Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "Could not start the technician console:\n\n" + ex.Message,
                "InnAware Support Technician",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void Navigate(string uri)
    {
        if (_web.CoreWebView2 is null)
            return;

        _web.CoreWebView2.Navigate(uri);
    }

    private void NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedUri(e.Uri))
            return;

        e.Cancel = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            _statusText.Text = "Blocked external navigation";
        }
    }

    private bool IsAllowedUri(string value)
    {
        if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !Uri.TryCreate(_server, UriKind.Absolute, out var server))
            return false;

        return string.Equals(uri.Scheme, server.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == server.Port;
    }

    private void NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!IsAllowedUri(e.Uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri)
                {
                    UseShellExecute = true
                });
            }
            catch { }
            return;
        }

        BeginInvoke((Action)(() =>
        {
            var child = new TechnicianForm(_server, e.Uri)
            {
                StartPosition = FormStartPosition.CenterParent
            };
            child.Show(this);
        }));
    }

    private void DownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var suggested = Path.GetFileName(e.ResultFilePath);
        using var dialog = new SaveFileDialog
        {
            FileName = string.IsNullOrWhiteSpace(suggested)
                ? "InnAware-Download"
                : suggested,
            OverwritePrompt = true,
            Title = "Save InnAware support file"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            e.ResultFilePath = dialog.FileName;
            e.Handled = true;
            _statusText.Text = "Downloading " + Path.GetFileName(dialog.FileName);
        }
        else
        {
            e.Cancel = true;
            e.Handled = true;
        }
    }

    private Task ClickWebButtonAsync(string id) =>
        ExecuteScriptAsync(
            $"document.getElementById({JsonString(id)})?.click();");

    private Task FocusWebElementAsync(string id) =>
        ExecuteScriptAsync(
            $"(()=>{{const e=document.getElementById({JsonString(id)});if(e){{e.scrollIntoView({{behavior:'smooth',block:'center'}});e.focus();}}}})();");

    private Task ScrollWebElementAsync(string id) =>
        ExecuteScriptAsync(
            $"document.getElementById({JsonString(id)})?.scrollIntoView({{behavior:'smooth',block:'center'}});");

    private static string MonitorValue(string value)
    {
        const string prefix = "Monitor ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return "0";

        var remainder = value[prefix.Length..];
        var digits = new string(
            remainder.TakeWhile(char.IsDigit).ToArray());

        return int.TryParse(digits, out var monitor) && monitor > 0
            ? (monitor - 1).ToString()
            : "0";
    }

    private Task SetWebSelectAsync(string id, string value) =>
        ExecuteScriptAsync(
            $"(()=>{{const e=document.getElementById({JsonString(id)});if(!e)return;e.value={JsonString(value)};e.dispatchEvent(new Event('change',{{bubbles:true}}));}})();");

    private async Task RefreshMonitorChoicesAsync()
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(()=>{const e=document.getElementById('monitorSelect');return e?[...e.options].map(o=>o.textContent||o.text):[];})()");
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;

            var items = doc.RootElement
                .EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray();

            if (items.Length == 0)
                return;

            var selected = _monitor.SelectedItem?.ToString();
            _monitor.Items.Clear();
            _monitor.Items.AddRange(items);
            var index = selected is null
                ? -1
                : Array.FindIndex(items, x =>
                    string.Equals(x, selected, StringComparison.Ordinal));
            _monitor.SelectedIndex = index >= 0 ? index : 0;
        }
        catch { }
    }

    private async Task SyncViewerControlsAsync()
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(()=>{const g=id=>document.getElementById(id)?.value??null;return {monitor:g('monitorSelect'),capture:g('captureModeSelect'),resolution:g('scaleSelect'),quality:g('qualitySelect'),fps:g('fpsSelect'),viewer:!!document.getElementById('viewerView')&&!document.getElementById('viewerView').classList.contains('hidden')};})()");
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("viewer", out var viewer) ||
                viewer.ValueKind != System.Text.Json.JsonValueKind.True)
                return;

            SetComboValue(_capture,
                root.GetProperty("capture").GetString() == "gdi"
                    ? "Compatibility (GDI)"
                    : "Auto (DXGI)");

            var scale = root.GetProperty("resolution").GetString() ?? "100";
            SetComboValue(_resolution, scale + "%");

            var quality = root.GetProperty("quality").GetString() ?? "55";
            SetComboValue(_quality, quality switch
            {
                "35" => "Low",
                "70" => "High",
                "85" => "Very high",
                _ => "Balanced"
            });

            var fps = root.GetProperty("fps").GetString() ?? "6";
            SetComboValue(_fps, fps == "0" ? "Adaptive" : fps + " FPS");

            if (root.TryGetProperty("monitor", out var monitorElement))
            {
                var monitorValue = monitorElement.GetString();
                if (int.TryParse(monitorValue, out var monitorIndex) &&
                    monitorIndex >= 0)
                {
                    var wanted = "Monitor " + (monitorIndex + 1);
                    for (var i = 0; i < _monitor.Items.Count; i++)
                    {
                        var text = _monitor.Items[i]?.ToString() ?? "";
                        if (text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            _monitor.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // The main console/login page does not expose viewer controls.
        }
    }

    private static void SetComboValue(
        ToolStripComboBox combo,
        string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(
                    combo.Items[i]?.ToString(),
                    value,
                    StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private async Task ExecuteScriptAsync(string script)
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            _statusText.Text = "Viewer action failed: " + ex.Message;
        }
    }

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private void ToggleNativeFullscreen()
    {
        if (!_nativeFullscreen)
        {
            _savedBorderStyle = FormBorderStyle;
            _savedWindowState = WindowState;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _tools.Visible = false;
            _status.Visible = false;
            _nativeFullscreen = true;
        }
        else
        {
            _tools.Visible = true;
            _status.Visible = true;
            FormBorderStyle = _savedBorderStyle;
            WindowState = _savedWindowState;
            _nativeFullscreen = false;
        }
    }
}
