namespace InnAwareSupport.Agent;

internal sealed record SupportChatMessage(
    long ID,
    string SenderType,
    string SenderName,
    string Body,
    DateTime CreatedAt,
    string AttachmentTransferID = "",
    string AttachmentName = "",
    string AttachmentMIME = "",
    long AttachmentSize = 0);

internal sealed class ChatForm : Form
{
    private sealed class ChatListItem
    {
        public SupportChatMessage Message { get; }

        public ChatListItem(SupportChatMessage message)
        {
            Message = message;
        }

        public override string ToString()
        {
            var who = string.IsNullOrWhiteSpace(Message.SenderName)
                ? Message.SenderType
                : Message.SenderName;
            var body = Message.Body ?? "";
            var attachment = string.IsNullOrWhiteSpace(Message.AttachmentTransferID)
                ? ""
                : $" [Image: {Message.AttachmentName} ({FormatBytes(Message.AttachmentSize)})]";
            return $"[{Message.CreatedAt.ToLocalTime():g}] {who}: {body}{attachment}".TrimEnd();
        }

        private static string FormatBytes(long value)
        {
            if (value < 1024) return $"{value} B";
            if (value < 1024 * 1024) return $"{value / 1024d:0.0} KB";
            return $"{value / (1024d * 1024d):0.00} MB";
        }
    }

    private readonly ListBox _messages = new();
    private readonly TextBox _input = new();
    private readonly Button _send = new();
    private readonly Button _attach = new();
    private readonly Button _openImage = new();

    public event EventHandler<string>? SendRequested;
    public event EventHandler? ImageSendRequested;
    public event EventHandler<SupportChatMessage>? AttachmentOpenRequested;

    public ChatForm()
    {
        Text = "InnAware Support Chat";
        Width = 560;
        Height = 640;
        MinimumSize = new Size(440, 450);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10F);

        _messages.Dock = DockStyle.Fill;
        _messages.HorizontalScrollbar = true;
        _messages.IntegralHeight = false;
        _messages.SelectedIndexChanged += (_, _) =>
        {
            _openImage.Enabled =
                _messages.SelectedItem is ChatListItem item &&
                !string.IsNullOrWhiteSpace(item.Message.AttachmentTransferID);
        };
        _messages.DoubleClick += (_, _) => OpenSelectedImage();

        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Padding = new Padding(10)
        };

        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.ScrollBars = ScrollBars.Vertical;
        _input.SetBounds(10, 10, 388, 92);
        _input.Anchor = AnchorStyles.Left | AnchorStyles.Top |
                        AnchorStyles.Right;
        _input.KeyDown += InputKeyDown;

        _send.Text = "Send";
        _send.SetBounds(408, 10, 120, 44);
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _send.Click += (_, _) => Submit();

        _attach.Text = "Attach Image";
        _attach.SetBounds(408, 58, 120, 44);
        _attach.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _attach.Click += (_, _) => ImageSendRequested?.Invoke(this, EventArgs.Empty);

        _openImage.Text = "Open Selected Image";
        _openImage.SetBounds(10, 108, 180, 32);
        _openImage.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _openImage.Enabled = false;
        _openImage.Click += (_, _) => OpenSelectedImage();

        var hint = new Label
        {
            Text = "Enter sends · Shift+Enter adds a line · Images: JPEG/PNG/GIF/WebP up to 5 MB",
            AutoSize = false,
            Left = 200,
            Top = 112,
            Width = 328,
            Height = 28,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8F)
        };

        bottom.Controls.AddRange([_input, _send, _attach, _openImage, hint]);
        Controls.Add(_messages);
        Controls.Add(bottom);

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing)
                return;
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
                _messages.Items.Add(new ChatListItem(message));
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
        _messages.Items.Add(new ChatListItem(message));
        _messages.TopIndex = Math.Max(0, _messages.Items.Count - 1);
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

    private void OpenSelectedImage()
    {
        if (_messages.SelectedItem is not ChatListItem item ||
            string.IsNullOrWhiteSpace(item.Message.AttachmentTransferID))
            return;
        AttachmentOpenRequested?.Invoke(this, item.Message);
    }
}
