using System.Drawing;
using System.Windows.Forms;

namespace PRISM.Agent.Tray;

/// <summary>
/// Floating log viewer that receives live output from <see cref="TrayLoggerProvider"/>.
/// Closing the window hides it rather than destroying it so it can be re-opened
/// from the tray context menu.
/// </summary>
public sealed class LogsForm : Form
{
    readonly TrayLoggerProvider _provider;
    readonly RichTextBox _rtb;

    public LogsForm(TrayLoggerProvider provider)
    {
        _provider = provider;

        Text            = "PRISM Agent — Logs";
        Size            = new Size(860, 520);
        MinimumSize     = new Size(500, 300);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(28, 28, 28);

        // Initialise the log output control first so the toolbar lambdas can
        // reference it without triggering a nullable dereference warning.
        _rtb = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BackColor   = Color.FromArgb(18, 18, 18),
            ForeColor   = Color.FromArgb(204, 204, 204),
            Font        = new Font("Consolas", 9f, FontStyle.Regular),
            ScrollBars  = RichTextBoxScrollBars.Both,
            WordWrap    = false,
            BorderStyle = BorderStyle.None,
        };

        // Toolbar
        var toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Color.FromArgb(40, 40, 40),
            Padding   = new Padding(6, 5, 6, 0),
        };
        var clearBtn = MakeButton("Clear");
        clearBtn.Click += (_, _) => _rtb.Clear();

        var copyBtn = MakeButton("Copy All");
        copyBtn.Left = clearBtn.Right + 6;
        copyBtn.Click += (_, _) =>
        {
            if (_rtb.Text.Length > 0)
                Clipboard.SetText(_rtb.Text);
        };
        toolbar.Controls.Add(clearBtn);
        toolbar.Controls.Add(copyBtn);

        Controls.Add(_rtb);
        Controls.Add(toolbar);

        // Load existing buffered lines.
        foreach (var line in _provider.GetSnapshot())
            AppendLine(line);

        _provider.OnLogLine += OnNewLine;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _provider.OnLogLine -= OnNewLine;
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------

    void OnNewLine(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLine(line)); return; }
        AppendLine(line);
    }

    void AppendLine(string line)
    {
        _rtb.AppendText(line + "\n");

        // Keep the live tail visible.
        _rtb.SelectionStart = _rtb.TextLength;
        _rtb.ScrollToCaret();

        // Hard-trim at ~2200 lines to stay within RichTextBox limits.
        const int max = 2000;
        const int trim = 200;
        if (_rtb.Lines.Length > max + trim)
        {
            var firstKeep = _rtb.GetFirstCharIndexFromLine(_rtb.Lines.Length - max);
            _rtb.Select(0, firstKeep);
            _rtb.SelectedText = "";
        }
    }

    static Button MakeButton(string text) => new()
    {
        Text      = text,
        Width     = 76,
        Height    = 26,
        Top       = 0,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(60, 60, 60),
        ForeColor = Color.White,
        Font      = new Font("Segoe UI", 8.5f),
    };
}
