using System.Drawing;
using System.Windows.Forms;

namespace PRISM.Agent.Tray;

/// <summary>
/// Non-modal progress dialog shown by the tray during
/// <see cref="Updater.DownloadAndInstallAsync"/>.
///
/// Two states:
///   - Downloading vX.Y.Z — N%   (driven by <see cref="IProgress{T}"/>)
///   - Installing…              (the PS helper has taken over;
///                               this form closes itself shortly after
///                               <see cref="Application.Exit"/>)
///
/// Closing the window cancels nothing — the user is told that the
/// install will continue in the spawned PowerShell window once the
/// download finishes.  We do not actually wire cancellation through
/// because <see cref="Updater"/> exits the process on completion
/// regardless.
/// </summary>
public sealed class UpdateProgressForm : Form
{
    readonly Label _headerLabel;
    readonly Label _statusLabel;
    readonly ProgressBar _bar;
    readonly Label _hintLabel;

    public UpdateProgressForm(string tagName)
    {
        Text            = "PRISM Agent — Updating";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = true;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(440, 168);
        BackColor       = Color.White;
        Font            = new Font("Segoe UI", 9f);

        _headerLabel = new Label
        {
            Text      = $"Updating to {tagName}",
            Font      = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40),
            Location  = new Point(20, 18),
            AutoSize  = true,
        };

        _statusLabel = new Label
        {
            Text     = "Preparing download…",
            Location = new Point(20, 50),
            Size     = new Size(400, 20),
            ForeColor = Color.FromArgb(80, 80, 80),
        };

        _bar = new ProgressBar
        {
            Location = new Point(20, 76),
            Size     = new Size(400, 22),
            Minimum  = 0,
            Maximum  = 100,
            Value    = 0,
            Style    = ProgressBarStyle.Continuous,
        };

        _hintLabel = new Label
        {
            Text     = "A console window will appear briefly to install the new version.",
            Location = new Point(20, 108),
            Size     = new Size(400, 40),
            ForeColor = Color.FromArgb(120, 120, 120),
            Font      = new Font("Segoe UI", 8.25f, FontStyle.Italic),
        };

        Controls.Add(_headerLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_bar);
        Controls.Add(_hintLabel);
    }

    /// <summary>
    /// Updates the progress bar and status label.  Safe to call from
    /// any thread.
    /// </summary>
    public void SetDownloadProgress(int percent)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => SetDownloadProgress(percent)); }
            catch (ObjectDisposedException) { /* form torn down — ignore */ }
            return;
        }

        var clamped = Math.Clamp(percent, 0, 100);
        _bar.Value = clamped;
        _statusLabel.Text = clamped < 100
            ? $"Downloading… {clamped}%"
            : "Download complete.";
    }

    /// <summary>
    /// Switches the form to its "PowerShell helper has taken over"
    /// state.  Called right before <see cref="Application.Exit"/>.
    /// </summary>
    public void SetInstalling()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(SetInstalling); }
            catch (ObjectDisposedException) { /* nop */ }
            return;
        }

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _statusLabel.Text = "Installing… the agent will restart automatically.";
    }

    /// <summary>
    /// Marks the operation as failed and lets the user dismiss the
    /// form.  Used when <see cref="Updater.DownloadAndInstallAsync"/>
    /// throws before <see cref="Application.Exit"/> fires.
    /// </summary>
    public void SetFailed(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => SetFailed(message)); }
            catch (ObjectDisposedException) { /* nop */ }
            return;
        }

        _bar.Style = ProgressBarStyle.Continuous;
        _bar.MarqueeAnimationSpeed = 0;
        _bar.Value = 0;
        _statusLabel.Text  = "Update failed.";
        _statusLabel.ForeColor = Color.FromArgb(192, 40, 40);
        _hintLabel.Text    = message;
    }
}
