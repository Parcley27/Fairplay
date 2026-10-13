namespace Fairplay.App;

/// <summary>Simple live log viewer.</summary>
sealed class LogForm : Form
{
    readonly TextBox _box;

    public LogForm()
    {
        Text = "Fairplay — log";
        Icon = TrayIcons.Get(TrayIcons.Kind.Streaming);
        Width = 900;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
        };
        Controls.Add(_box);
        _box.Lines = LogBuffer.Snapshot();
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
        LogBuffer.Line += OnLine;
        FormClosed += (_, _) => LogBuffer.Line -= OnLine;
    }

    void OnLine(string line)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            _box.AppendText(line + Environment.NewLine);
        });
    }
}
