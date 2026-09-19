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

    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private bool _nativeFullscreen;

    public TechnicianForm(string server, string? startUrl = null)
    {
        _server = server.TrimEnd('/');
        _startUrl = string.IsNullOrWhiteSpace(startUrl)
            ? _server
            : startUrl;

        Text = "InnAware Support Technician";
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
            core.NavigationCompleted += (_, e) =>
            {
                _statusText.Text = e.IsSuccess
                    ? "Connected to InnAware Support"
                    : $"Navigation error: {e.WebErrorStatus}";
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
