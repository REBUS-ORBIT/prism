using System.Drawing;
using System.Windows.Forms;
using PRISM.Agent.Config;

namespace PRISM.Agent.Tray;

/// <summary>
/// Modal settings dialog — built entirely in code, no designer files.
/// Changes are applied to the shared <see cref="AgentConfig"/> and saved
/// on OK; cancelled if the user clicks Cancel or closes the window.
/// </summary>
public sealed class SettingsForm : Form
{
    readonly AgentConfig _cfg;
    readonly TextBox     _urlBox;
    readonly TextBox     _nodeBox;
    readonly ComboBox    _rhinoVerBox;
    readonly TextBox     _logDirBox;

    public SettingsForm(AgentConfig cfg)
    {
        _cfg = cfg;

        Text            = "PRISM Agent — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(480, 240);
        Font            = new Font("Segoe UI", 9f);

        // ---- layout ----
        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            Padding     = new Padding(14, 12, 14, 8),
            ColumnCount = 2,
            RowCount    = 5,
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        // Server URL
        tbl.Controls.Add(MakeLabel("Server URL:"),  0, 0);
        _urlBox = MakeTextBox(cfg.PrismUrl);
        tbl.Controls.Add(_urlBox, 1, 0);

        // Node name
        tbl.Controls.Add(MakeLabel("Node Name:"),   0, 1);
        _nodeBox = MakeTextBox(cfg.NodeName);
        tbl.Controls.Add(_nodeBox, 1, 1);

        // Rhino version
        tbl.Controls.Add(MakeLabel("Rhino Version:"), 0, 2);
        _rhinoVerBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock          = DockStyle.Fill,
        };
        _rhinoVerBox.Items.AddRange(new[] { "auto", "8", "9" });
        _rhinoVerBox.SelectedItem = cfg.RhinoVersion ?? "auto";
        if (_rhinoVerBox.SelectedIndex < 0) _rhinoVerBox.SelectedIndex = 0;
        tbl.Controls.Add(_rhinoVerBox, 1, 2);

        // Log directory
        tbl.Controls.Add(MakeLabel("Log Directory:"), 0, 3);
        var logRow = new Panel { Dock = DockStyle.Fill };
        _logDirBox = MakeTextBox(cfg.LogDir);
        _logDirBox.Dock = DockStyle.Fill;
        var browseBtn = new Button { Text = "…", Width = 28, Dock = DockStyle.Right, FlatStyle = FlatStyle.System };
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _logDirBox.Text };
            if (dlg.ShowDialog() == DialogResult.OK) _logDirBox.Text = dlg.SelectedPath;
        };
        logRow.Controls.Add(_logDirBox);
        logRow.Controls.Add(browseBtn);
        tbl.Controls.Add(logRow, 1, 3);

        // Buttons
        var btnRow = new FlowLayoutPanel
        {
            Dock            = DockStyle.Fill,
            FlowDirection   = FlowDirection.RightToLeft,
            WrapContents    = false,
        };
        var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, DialogResult = DialogResult.Cancel };
        var ok     = new Button { Text = "OK",     Width = 80, Height = 28, DialogResult = DialogResult.OK };
        btnRow.Controls.Add(cancel);
        btnRow.Controls.Add(ok);
        tbl.Controls.Add(btnRow, 1, 4);

        Controls.Add(tbl);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
            ApplyAndSave();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Returns true if any field that requires a WS reconnect was changed.
    /// Must be called BEFORE <see cref="ShowDialog"/> or capture the previous values beforehand.
    /// </summary>
    public bool HasConnectionChange(string prevUrl, string prevNode) =>
        _urlBox.Text.Trim()  != prevUrl  ||
        _nodeBox.Text.Trim() != prevNode;

    // ------------------------------------------------------------------

    void ApplyAndSave()
    {
        _cfg.PrismUrl     = _urlBox.Text.Trim();
        _cfg.NodeName     = _nodeBox.Text.Trim();
        _cfg.RhinoVersion = _rhinoVerBox.SelectedItem?.ToString() ?? "auto";
        _cfg.LogDir       = _logDirBox.Text.Trim();
        _cfg.Save();
    }

    static Label MakeLabel(string text) => new()
    {
        Text      = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock      = DockStyle.Fill,
        AutoSize  = false,
    };

    static TextBox MakeTextBox(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
    };
}
