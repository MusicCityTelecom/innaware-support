using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace InnAwareSupport.Technician;

internal sealed class TechnicianForm : Form
{
    private readonly string _consoleUrl;
    private readonly WebView2 _web = new();
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripLabel _status = new();
    private CoreWebView2Environment? _environment;

    public TechnicianForm(string consoleUrl)
    {
        _consoleUrl = consoleUrl.TrimEnd('/');
        Text = "InnAware Support Technician";
        Width = 1500;
        Height = 950;
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        BuildToolbar();

        _web.Dock = DockStyle.Fill;
        _web.NavigationStarting += (_, e) =>
        {
            _status.Text = "Loading…";
            if (!IsTrustedUri(e.Uri))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        _web.NavigationCompleted += (_, e) =>
        {
            _status.Text = e.IsSuccess ? "Connected" : $"Navigation error: {e.WebErrorStatus}";
        };

        Controls.Add(_web);
        Controls.Add(_toolbar);

        Shown += async (_, _) => await InitializeAsync();
    }

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(6, 4, 6, 4);
        _toolbar.AutoSize = false;
        _toolbar.Height = 42;

        AddButton("Console", async () => await NavigateConsoleAsync());
        AddButton("Back", async () =>
        {
            if (_web.CanGoBack) _web.GoBack();
            await Task.CompletedTask;
        });
        AddButton("Refresh", async () =>
        {
            _web.Reload();
            await Task.CompletedTask;
        });

        _toolbar.Items.Add(new ToolStripSeparator());

        AddButton("Detach", async () => await ClickWebControlAsync("popoutViewerButton"));
        AddButton("Fit", async () => await ClickWebControlAsync("fitViewButton"));
        AddButton("1:1", async () => await ClickWebControlAsync("actualViewButton"));
        AddButton("Fullscreen", async () => await ClickWebControlAsync("fullscreenButton"));

        _toolbar.Items.Add(new ToolStripSeparator());

        AddButton("Record", async () => await ClickWebControlAsync("recordViewerButton"));
        AddButton("Elevation", async () => await ClickWebControlAsync("requestElevationButton"));
        AddButton("Chat", async () => await ScrollWebControlIntoViewAsync("chatCard"));
        AddButton("Files", async () => await ScrollWebControlIntoViewAsync("fileTransferCard"));

        _toolbar.Items.Add(new ToolStripSeparator());

        AddButton("Open in browser", async () =>
        {
            OpenExternal(_web.Source?.ToString() ?? _consoleUrl);
            await Task.CompletedTask;
        });

        _status.Alignment = ToolStripItemAlignment.Right;
        _status.Text = "Starting…";
        _toolbar.Items.Add(_status);
    }

    private void AddButton(string text, Func<Task> action)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
        };
        _toolbar.Items.Add(button);
    }

    internal async Task InitializeAsync()
    {
        if (_web.CoreWebView2 is not null)
            return;

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InnAware",
                "Technician",
                "WebView2");

            Directory.CreateDirectory(userData);

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);

            await _web.EnsureCoreWebView2Async(_environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;

            core.NewWindowRequested += CoreNewWindowRequested;
            core.DownloadStarting += (_, e) =>
            {
                _status.Text = $"Downloading {Path.GetFileName(e.ResultFilePath)}…";
            };

            core.NavigationCompleted += (_, _) =>
            {
                _status.Text = "Connected";
            };

            core.Navigate(_consoleUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show(
                this,
                "Microsoft Edge WebView2 Runtime is required for the InnAware technician client.\n\nOpen the Microsoft WebView2 Runtime download page now?",
                "WebView2 Runtime Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (answer == DialogResult.Yes)
                OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

            _status.Text = "WebView2 Runtime required";
        }
        catch (Exception ex)
        {
            _status.Text = "Startup failed";
            MessageBox.Show(
                this,
                ex.Message,
                "InnAware Technician",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async void CoreNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri) || !IsTrustedUri(e.Uri))
        {
            e.Handled = true;
            OpenExternal(e.Uri);
            return;
        }

        e.Handled = true;

        var viewer = new TechnicianForm(_consoleUrl)
        {
            Text = "InnAware Detached Support Viewer"
        };

        viewer.Show(this);
        await viewer.InitializeAsync();

        if (viewer._web.CoreWebView2 is not null)
            viewer._web.CoreWebView2.Navigate(e.Uri);
    }

    private async Task NavigateConsoleAsync()
    {
        if (_web.CoreWebView2 is null)
        {
            await InitializeAsync();
            return;
        }

        _web.CoreWebView2.Navigate(_consoleUrl);
    }

    private async Task ClickWebControlAsync(string id)
    {
        if (_web.CoreWebView2 is null)
            return;

        var script =
            $"(() => {{ const el=document.getElementById({System.Text.Json.JsonSerializer.Serialize(id)}); if(!el) return false; el.click(); return true; }})()";

        var result = await _web.ExecuteScriptAsync(script);
        if (string.Equals(result, "false", StringComparison.OrdinalIgnoreCase))
            _status.Text = "Open a live session first";
    }

    private async Task ScrollWebControlIntoViewAsync(string id)
    {
        if (_web.CoreWebView2 is null)
            return;

        var script =
            $"(() => {{ const el=document.getElementById({System.Text.Json.JsonSerializer.Serialize(id)}); if(!el || el.classList.contains('hidden')) return false; el.scrollIntoView({{behavior:'smooth',block:'center'}}); return true; }})()";

        var result = await _web.ExecuteScriptAsync(script);
        if (string.Equals(result, "false", StringComparison.OrdinalIgnoreCase))
            _status.Text = "That tool is not available in the current session";
    }

    private bool IsTrustedUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (!Uri.TryCreate(_consoleUrl, UriKind.Absolute, out var allowed))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, allowed.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(value)
            {
                UseShellExecute = true
            });
        }
        catch { }
    }
}
