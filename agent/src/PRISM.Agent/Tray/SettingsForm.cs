using System.Drawing;
using System.Windows.Forms;
using PRISM.Agent.Config;
using PRISM.Contracts;

namespace PRISM.Agent.Tray;

/// <summary>
/// Settings dialog — built entirely in code, no designer files.
/// Changes are applied to the shared <see cref="AgentConfig"/> and saved
/// on OK; cancelled if the user clicks Cancel or closes the window.
///
/// As of v0.1.37 the form is <b>non-modal and resizable</b> so operators
/// can leave the settings open alongside other windows while the
/// Visualiser group box is being filled in.
/// </summary>
public sealed class SettingsForm : Form
{
    readonly AgentConfig _cfg;
    readonly TextBox     _urlBox;
    readonly TextBox     _nodeBox;
    readonly ComboBox    _rhinoVerBox;
    readonly TextBox     _logDirBox;

    // ---- Visualiser group ----
    readonly TextBox     _ueRootBox;
    readonly TextBox     _ueTemplateTagBox;
    readonly NumericUpDown _maxConcurrentBox;
    readonly CheckBox    _gpuCheckBox;

    public SettingsForm(AgentConfig cfg)
    {
        _cfg = cfg;

        Text            = "PRISM Agent — Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        MinimizeBox     = true;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(560, 460);
        MinimumSize     = new Size(480, 420);
        Font            = new Font("Segoe UI", 9f);

        // ---- layout ----
        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            Padding     = new Padding(14, 12, 14, 8),
            ColumnCount = 2,
            RowCount    = 6,
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        // Row 4: Visualiser group (auto-size; ~180 px tall)
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // Row 5: buttons
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

        // Visualiser group — spans both columns, only enabled when the Visualiser role is on
        var visualiserGroup = new GroupBox
        {
            Text   = "Visualiser (Phase A — orchestrator not yet implemented)",
            Dock   = DockStyle.Fill,
            Padding = new Padding(10, 8, 10, 8),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var vtbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 4,
            AutoSize    = true,
        };
        vtbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        vtbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            vtbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        vtbl.Controls.Add(MakeLabel("UE root:"), 0, 0);
        _ueRootBox = MakeTextBox(cfg.UnrealEngineRoot);
        vtbl.Controls.Add(_ueRootBox, 1, 0);

        vtbl.Controls.Add(MakeLabel("Template tag:"), 0, 1);
        _ueTemplateTagBox = MakeTextBox(cfg.UnrealTemplateTag);
        vtbl.Controls.Add(_ueTemplateTagBox, 1, 1);

        vtbl.Controls.Add(MakeLabel("Max concurrent:"), 0, 2);
        _maxConcurrentBox = new NumericUpDown
        {
            Minimum = 1, Maximum = 4,
            Value   = Math.Max(1, Math.Min(4, cfg.VisualiserMaxConcurrent)),
            Dock    = DockStyle.Fill,
        };
        vtbl.Controls.Add(_maxConcurrentBox, 1, 2);

        vtbl.Controls.Add(MakeLabel("GPU check:"), 0, 3);
        _gpuCheckBox = new CheckBox
        {
            Text    = "Require discrete GPU at startup",
            Checked = cfg.VisualiserGpuCheck,
            Dock    = DockStyle.Fill,
            AutoSize = false,
        };
        vtbl.Controls.Add(_gpuCheckBox, 1, 3);

        visualiserGroup.Controls.Add(vtbl);
        tbl.Controls.Add(visualiserGroup, 0, 4);
        tbl.SetColumnSpan(visualiserGroup, 2);

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
        tbl.Controls.Add(btnRow, 1, 5);

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
        _cfg.UnrealEngineRoot        = _ueRootBox.Text.Trim();
        _cfg.UnrealTemplateTag       = _ueTemplateTagBox.Text.Trim();
        _cfg.VisualiserMaxConcurrent = (int)_maxConcurrentBox.Value;
        _cfg.VisualiserGpuCheck      = _gpuCheckBox.Checked;
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
