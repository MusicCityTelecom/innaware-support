namespace InnAwareSupport.Agent;

internal sealed record SupportChatMessage(
    long ID,
    string SenderType,
    string SenderName,
    string Body,
    DateTime CreatedAt);

internal sealed class ChatForm : Form
{
    private readonly ListBox _messages = new();
    private readonly TextBox _input = new();
    private readonly Button _send = new();

    public event EventHandler<string>? SendRequested;

    public ChatForm()
    {
        Text = "InnAware Support Chat";
        Width = 520;
        Height = 600;
        MinimumSize = new Size(420, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);

        _messages.Dock = DockStyle.Fill;
        _messages.HorizontalScrollbar = true;
        _messages.IntegralHeight = false;

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 118,
            Padding = new Padding(10)
        };

        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.ScrollBars = ScrollBars.Vertical;
        _input.SetBounds(10, 10, 375, 88);
        _input.Anchor = AnchorStyles.Left | AnchorStyles.Top |
                        AnchorStyles.Right | AnchorStyles.Bottom;
        _input.KeyDown += InputKeyDown;

        _send.Text = "Send";
        _send.SetBounds(395, 10, 95, 88);
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Right |
                       AnchorStyles.Bottom;
        _send.Click += (_, _) => Submit();

        bottom.Controls.AddRange([_input, _send]);
        Controls.Add(_messages);
        Controls.Add(bottom);

        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void SetMessages(IEnumerable<SupportChatMessage> messages)
    {
        _messages.BeginUpdate();
        try
        {
            _messages.Items.Clear();
            foreach (var message in messages)
                _messages.Items.Add(FormatMessage(message));
        }
        finally
        {
            _messages.EndUpdate();
        }

        if (_messages.Items.Count > 0)
            _messages.TopIndex = _messages.Items.Count - 1;
    }

    public void AppendMessage(SupportChatMessage message)
    {
        _messages.Items.Add(FormatMessage(message));
        _messages.TopIndex = Math.Max(0, _messages.Items.Count - 1);
    }

    private static string FormatMessage(SupportChatMessage message)
    {
        var who = string.IsNullOrWhiteSpace(message.SenderName)
            ? message.SenderType
            : message.SenderName;

        return $"[{message.CreatedAt.ToLocalTime():g}] {who}: {message.Body}";
    }

    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
            return;

        e.SuppressKeyPress = true;
        Submit();
    }

    private void Submit()
    {
        var body = _input.Text.Trim();
        if (body.Length == 0)
            return;

        if (body.Length > 4000)
        {
            MessageBox.Show(
                this,
                "Chat messages are limited to 4,000 characters.",
                "InnAware Support Chat",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SendRequested?.Invoke(this, body);
        _input.Clear();
    }
}
