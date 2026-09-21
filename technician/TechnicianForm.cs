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
    private readonly ToolStripComboBox _video = new();
    private readonly ToolStripComboBox _resolution = new();
    private readonly ToolStripComboBox _quality = new();
    private readonly ToolStripComboBox _fps = new();

    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private bool _nativeFullscreen;

    public TechnicianForm(string server, string? startUrl = null)
    {
        _server = server.TrimEnd('/');
        _startUrl = string.IsNullOrWhiteSpace(startUrl)
            ? _server
            : startUrl;

        Text = $"InnAware Support Technician {TechnicianBuildInfo.Version}";
        Width = 1500;
        Height = 950;
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();

        _web.Dock = DockStyle.Fill;
        _statusText.Text =
            $"Starting technician console · {TechnicianBuildInfo.InformationalVersion}";
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
    }

    private void BuildToolbar()
    {
        _tools.GripStyle = ToolStripGripStyle.Hidden;
        _tools.Padding = new Padding(6, 4, 6, 4);
        _tools.AutoSize = true;

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

        _tools.Items.Add(new ToolStripLabel("Monitor"));
        _monitor.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitor.AutoSize = false;
        _monitor.Width = 150;
        _monitor.Items.Add(new SelectorChoice("Monitor 1", "0"));
        _monitor.SelectedIndex = 0;
        _monitor.ComboBox.DropDown += async (_, _) =>
            await SyncMonitorSelectorAsync();
        _monitor.ComboBox.SelectionChangeCommitted += async (_, _) =>
        {
            if (_monitor.SelectedItem is SelectorChoice selected)
                await SetWebSelectValueAsync("monitorSelect", selected.Value);
        };
        _tools.Items.Add(_monitor);

        ConfigureSelector(
            "Capture",
            _capture,
            "captureModeSelect",
            [
                new("Auto", "auto"),
                new("GDI", "gdi")
            ],
            "auto",
            82);

        ConfigureSelector(
            "Video",
            _video,
            "videoTransportSelect",
            [
                new("JPEG", "jpeg"),
                new("H.264", "h264-annexb")
            ],
            "jpeg",
            72);

        ConfigureSelector(
            "Resolution",
            _resolution,
            "scaleSelect",
            [
                new("100%", "100"),
                new("75%", "75"),
                new("50%", "50")
            ],
            "100",
            66);

        ConfigureSelector(
            "Quality",
            _quality,
            "qualitySelect",
            [
                new("Low", "35"),
                new("Balanced", "55"),
                new("High", "70"),
                new("Very high", "85")
            ],
            "55",
            86);

        ConfigureSelector(
            "FPS",
            _fps,
            "fpsSelect",
            [
                new("Adaptive", "0"),
                new("2", "2"),
                new("4", "4"),
                new("6", "6"),
                new("8", "8"),
                new("10", "10"),
                new("12", "12")
            ],
            "6",
            78);

        _tools.Items.Add(new ToolStripSeparator());

        AddButton("Detach", async (_, _) =>
            await ClickWebButtonAsync("popoutViewerButton"));
        AddButton("Fit", async (_, _) =>
            await ClickWebButtonAsync("fitViewButton"));
        AddButton("1:1", async (_, _) =>
            await ClickWebButtonAsync("actualViewButton"));
        AddButton("Record", async (_, _) =>
            await ClickWebButtonAsync("recordViewerButton"));
        AddButton("Chat", async (_, _) =>
            await FocusWebElementAsync("chatBody"));
        AddButton("Files", async (_, _) =>
            await ScrollWebElementAsync("fileTransferCard"));
        AddButton("Network", async (_, _) =>
            await ScrollWebElementAsync("networkCard"));
        AddButton("Refresh Net", async (_, _) =>
            await ClickWebButtonAsync("refreshNetworkButton"));
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

    private sealed record SelectorChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private void ConfigureSelector(
        string label,
        ToolStripComboBox combo,
        string webElementId,
        SelectorChoice[] choices,
        string defaultValue,
        int width)
    {
        _tools.Items.Add(new ToolStripLabel(label));

        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.AutoSize = false;
        combo.Width = width;

        foreach (var choice in choices)
            combo.Items.Add(choice);

        combo.SelectedItem = choices.First(x => x.Value == defaultValue);
        combo.ComboBox.SelectionChangeCommitted += async (_, _) =>
        {
            if (combo.SelectedItem is SelectorChoice selected)
                await SetWebSelectValueAsync(webElementId, selected.Value);
        };

        _tools.Items.Add(combo);
    }

    private async Task SetWebSelectValueAsync(string id, string value)
    {
        await ExecuteScriptAsync(
            $"(()=>{{const e=document.getElementById({JsonString(id)});if(!e)return false;e.value={JsonString(value)};e.dispatchEvent(new Event('change',{{bubbles:true}}));return true;}})();");
        await Task.Delay(75);
        await SyncViewerToolbarAsync();
    }

    private async Task SyncViewerToolbarAsync()
    {
        await SyncMonitorSelectorAsync();
        await SyncSelectorAsync(_capture, "captureModeSelect");
        await SyncSelectorAsync(_video, "videoTransportSelect");
        await SyncSelectorAsync(_resolution, "scaleSelect");
        await SyncSelectorAsync(_quality, "qualitySelect");
        await SyncSelectorAsync(_fps, "fpsSelect");
    }

    private sealed record BrowserSelectOption(
        string Label,
        string Value,
        bool Selected);

    private async Task SyncMonitorSelectorAsync()
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(()=>{const e=document.getElementById('monitorSelect');" +
                "if(!e)return '';" +
                "return JSON.stringify(Array.from(e.options).map(o=>({" +
                "Label:o.textContent||o.text||o.value," +
                "Value:o.value," +
                "Selected:o.selected" +
                "})));})();");

            var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var options =
                System.Text.Json.JsonSerializer.Deserialize<BrowserSelectOption[]>(
                    json);

            if (options is null || options.Length == 0)
                return;

            var current =
                options.FirstOrDefault(x => x.Selected)?.Value
                ?? options[0].Value;

            _monitor.ComboBox.BeginUpdate();
            try
            {
                _monitor.Items.Clear();
                foreach (var option in options)
                    _monitor.Items.Add(
                        new SelectorChoice(option.Label, option.Value));

                foreach (var item in _monitor.Items)
                {
                    if (item is SelectorChoice choice &&
                        string.Equals(
                            choice.Value,
                            current,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _monitor.SelectedItem = choice;
                        break;
                    }
                }

                if (_monitor.SelectedIndex < 0 && _monitor.Items.Count > 0)
                    _monitor.SelectedIndex = 0;
            }
            finally
            {
                _monitor.ComboBox.EndUpdate();
            }
        }
        catch
        {
            // Login/console pages do not expose the live monitor selector.
        }
    }

    private async Task SyncSelectorAsync(
        ToolStripComboBox combo,
        string webElementId)
    {
        if (_web.CoreWebView2 is null)
            return;

        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                $"document.getElementById({JsonString(webElementId)})?.value ?? '';");
            var value = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(value))
                return;

            foreach (var item in combo.Items)
            {
                if (item is SelectorChoice choice &&
                    string.Equals(
                        choice.Value,
                        value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = choice;
                    break;
                }
            }
        }
        catch
        {
            // The normal console/login pages do not expose viewer selectors.
        }
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
                    ? $"Connected to InnAware Support · {TechnicianBuildInfo.InformationalVersion}"
                    : $"Navigation error: {e.WebErrorStatus} · {TechnicianBuildInfo.InformationalVersion}";

                if (e.IsSuccess)
                {
                    await Task.Delay(150);
                    await SyncViewerToolbarAsync();
                }
            };
            core.SourceChanged += async (_, _) =>
            {
                _statusText.Text = core.Source;
                await Task.Delay(100);
                await SyncViewerToolbarAsync();
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
