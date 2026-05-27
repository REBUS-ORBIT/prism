using System.Drawing;
using System.Windows.Forms;

namespace PRISM.Agent.Tray;

/// <summary>
/// Modal "Update available" prompt that replaces the v0.1.32-era
/// <see cref="MessageBox"/>.  Shows the new tag, download size when
/// known, and a preview of the GitHub release body (release notes) so
/// the operator can decide whether to apply now or defer.
///
/// Two outcomes: <see cref="DialogResult.OK"/> when the user clicks
/// "Update now", <see cref="DialogResult.Cancel"/> for everything else
/// (Cancel button, X, Esc).
/// </summary>
public sealed class UpdateAvailableDialog : Form
{
    public UpdateAvailableDialog(Updater.UpdateInfo info, string currentVersion)
    {
        Text            = "PRISM Agent — Update Available";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(520, 360);
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);

        var header = new Label
        {
            Text      = $"PRISM Agent {info.TagName} is available",
            Font      = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40),
            Location  = new Point(20, 18),
            AutoSize  = true,
        };
        Controls.Add(header);

        var current = new Label
        {
            Text     = $"Currently running: v{currentVersion}",
            Location = new Point(20, 50),
            Size     = new Size(480, 18),
            ForeColor = Color.FromArgb(110, 110, 110),
        };
        Controls.Add(current);

        var sizeText = info.SizeBytes is long bytes && bytes > 0
            ? $"Download size: {FormatBytes(bytes)}"
            : "Download size: unknown";
        var size = new Label
        {
            Text     = sizeText,
            Location = new Point(20, 70),
            Size     = new Size(480, 18),
            ForeColor = Color.FromArgb(110, 110, 110),
        };
        Controls.Add(size);

        var notesHeader = new Label
        {
            Text      = "Release notes:",
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(60, 60, 60),
            Location  = new Point(20, 100),
            AutoSize  = true,
        };
        Controls.Add(notesHeader);

        var notes = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor   = Color.FromArgb(248, 248, 248),
            ForeColor   = Color.FromArgb(40, 40, 40),
            Location    = new Point(20, 122),
            Size        = new Size(480, 170),
            Font        = new Font("Consolas", 8.5f),
            Text        = string.IsNullOrWhiteSpace(info.Notes)
                ? "(no release notes published)"
                : Truncate(info.Notes!.Replace("\r\n", "\n").Replace("\n", "\r\n"), 4000),
        };
        Controls.Add(notes);

        var hint = new Label
        {
            Text      = "The agent will exit and re-launch automatically.",
            Location  = new Point(20, 302),
            Size      = new Size(360, 18),
            ForeColor = Color.FromArgb(120, 120, 120),
            Font      = new Font("Segoe UI", 8.25f, FontStyle.Italic),
        };
        Controls.Add(hint);

        var cancel = new Button
        {
            Text         = "Cancel",
            Width        = 92,
            Height       = 30,
            Location     = new Point(312, 320),
            FlatStyle    = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };
        var update = new Button
        {
            Text         = "Update now",
            Width        = 104,
            Height       = 30,
            Location     = new Point(408, 320),
            FlatStyle    = FlatStyle.System,
            DialogResult = DialogResult.OK,
        };
        Controls.Add(cancel);
        Controls.Add(update);

        AcceptButton = update;
        CancelButton = cancel;
    }

    static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "\r\n…(truncated)";
}
