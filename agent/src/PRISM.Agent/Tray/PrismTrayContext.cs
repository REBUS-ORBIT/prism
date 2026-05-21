using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PRISM.Agent.Config;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent.Tray;

/// <summary>
/// WinForms <see cref="ApplicationContext"/> that owns the system-tray icon,
/// context menu, and the lifetime of the .NET generic host.
///
/// State machine:
///   Connecting (amber) → Connected (green) ↔ Connecting (amber)
///   Stop Agent → Stopped (grey)
///   Start Agent → Connecting (amber) → Connected (green)
/// </summary>
public sealed class PrismTrayContext : ApplicationContext
{
    readonly IHost               _host;
    readonly AgentConfig         _cfg;
    readonly WsClient            _ws;
    readonly TrayLoggerProvider  _logProvider;
    readonly NotifyIcon          _tray;
    readonly ContextMenuStrip    _menu;

    // Menu items that need live updates
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _nodeItem;
    readonly ToolStripMenuItem _slotsItem;
    readonly ToolStripMenuItem _toggleItem;
    readonly ToolStripMenuItem _convItem;
    readonly ToolStripMenuItem _layItem;
    readonly ToolStripMenuItem _rcvItem;

    // Lazy-created forms (they hide on close, never destroyed until exit)
    LogsForm?     _logsForm;
    SettingsForm? _settingsForm;

    // Used to marshal callbacks from WsClient (ThreadPool) → WinForms message loop.
    readonly Control _sync = new Panel();

    bool _agentRunning = true;

    // -----------------------------------------------------------------------
    // Pre-built tray icons
    // -----------------------------------------------------------------------
    static readonly Icon _greenIcon  = MakeCircleIcon(Color.FromArgb(76,  175,  80));
    static readonly Icon _amberIcon  = MakeCircleIcon(Color.FromArgb(255, 152,   0));
    static readonly Icon _greyIcon   = MakeCircleIcon(Color.FromArgb(140, 140, 140));

    // -----------------------------------------------------------------------

    public PrismTrayContext(IHost host, AgentConfig cfg, TrayLoggerProvider logProvider)
    {
        _host        = host;
        _cfg         = cfg;
        _logProvider = logProvider;
        _ws          = host.Services.GetRequiredService<WsClient>();

        // Ensure the dummy control has a Windows handle so BeginInvoke works
        // from background threads before the first real window is created.
        _sync.CreateControl();

        // ---- Context menu ----
        _menu = new ContextMenuStrip { ShowImageMargin = false };

        var header = new ToolStripMenuItem("● PRISM Agent")
        {
            Enabled = false,
            Font    = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };
        Add(header);
        Add(new ToolStripSeparator());

        _statusItem = new ToolStripMenuItem("Status: Connecting…") { Enabled = false };
        Add(_statusItem);
        Add(new ToolStripSeparator());

        _nodeItem = new ToolStripMenuItem($"Node: {_cfg.NodeName}") { Enabled = false };
        Add(_nodeItem);

        _slotsItem = new ToolStripMenuItem($"Workers: {_cfg.Slots}");
        _slotsItem.DropDownItems.Add(new ToolStripMenuItem("▲  Add slot",    null, (_, _) => AdjustSlots(+1)));
        _slotsItem.DropDownItems.Add(new ToolStripMenuItem("▼  Remove slot", null, (_, _) => AdjustSlots(-1)));
        Add(_slotsItem);
        Add(new ToolStripSeparator());

        _convItem = MakeRoleItem("Conversion", AgentRole.Conversion);
        _layItem  = MakeRoleItem("Layering",   AgentRole.Layering);
        _rcvItem  = MakeRoleItem("Receive",    AgentRole.Receive);
        Add(_convItem);
        Add(_layItem);
        Add(_rcvItem);
        Add(new ToolStripSeparator());

        Add(new ToolStripMenuItem("⚙  Settings…",         null, OnSettings));
        Add(new ToolStripMenuItem("📋  View Logs…",        null, (_, _) => ShowLogs()));
        Add(new ToolStripMenuItem("🔄  Check for Updates", null, OnCheckUpdate));
        Add(new ToolStripSeparator());

        _toggleItem = new ToolStripMenuItem("■  Stop Agent", null, OnToggleAgent);
        Add(_toggleItem);
        Add(new ToolStripSeparator());

        Add(new ToolStripMenuItem("✕  Exit", null, OnExit));

        // ---- Tray icon ----
        _tray = new NotifyIcon
        {
            Icon             = _amberIcon,
            Text             = "PRISM Agent — Connecting…",
            Visible          = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowLogs();

        // ---- Subscribe to WS state changes ----
        _ws.OnReconnected  += () => _sync.BeginInvoke(() => ApplyState(TrayState.Connected));
        _ws.OnDisconnected += () => _sync.BeginInvoke(() =>
        {
            // Only revert to Connecting if the agent is supposed to be running.
            if (_agentRunning) ApplyState(TrayState.Connecting);
        });

        // ---- Start the host (non-blocking) ----
        _ = _host.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _sync.BeginInvoke(() =>
                    MessageBox.Show($"Host failed to start:\n{t.Exception?.InnerException?.Message}",
                        "PRISM Agent Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
        });
    }

    // -----------------------------------------------------------------------
    // State updates
    // -----------------------------------------------------------------------

    enum TrayState { Connected, Connecting, Stopped }

    void ApplyState(TrayState state)
    {
        _tray.Icon = state switch
        {
            TrayState.Connected  => _greenIcon,
            TrayState.Connecting => _amberIcon,
            TrayState.Stopped    => _greyIcon,
            _                    => _amberIcon,
        };
        var label = state switch
        {
            TrayState.Connected  => "Connected",
            TrayState.Connecting => "Connecting…",
            TrayState.Stopped    => "Stopped",
            _                    => "Unknown",
        };
        _statusItem.Text = $"Status: {label}";
        _tray.Text       = $"PRISM Agent — {label}";
    }

    // -----------------------------------------------------------------------
    // Menu actions
    // -----------------------------------------------------------------------

    void AdjustSlots(int delta)
    {
        var next = Math.Max(1, Math.Min(8, _cfg.Slots + delta));
        if (next == _cfg.Slots) return;
        _cfg.Slots      = next;
        _slotsItem.Text = $"Workers: {_cfg.Slots}";
        _cfg.Save();
        SendHello();
    }

    void UpdateRoles()
    {
        var roles = new List<AgentRole>();
        if (_convItem.Checked) roles.Add(AgentRole.Conversion);
        if (_layItem.Checked)  roles.Add(AgentRole.Layering);
        if (_rcvItem.Checked)  roles.Add(AgentRole.Receive);
        _cfg.Roles = roles.ToArray();
        _cfg.Save();
        SendHello();
    }

    void SendHello()
    {
        _ = _ws.SendAsync(MessageType.Hello, new HelloData
        {
            MachineId    = _cfg.MachineId,
            NodeName     = _cfg.NodeName,
            Slots        = _cfg.Slots,
            Roles        = _cfg.Roles,
            Formats      = AgentService.SupportedFormats,
            AgentVersion = typeof(PrismTrayContext).Assembly.GetName().Version?.ToString() ?? "0.1.0",
        });
    }

    void OnSettings(object? sender, EventArgs e)
    {
        // Always rebuild so fields show the latest values.
        _settingsForm?.Dispose();
        _settingsForm = new SettingsForm(_cfg);

        var prevUrl  = _cfg.PrismUrl;
        var prevNode = _cfg.NodeName;

        if (_settingsForm.ShowDialog() != DialogResult.OK) return;

        // SettingsForm.ApplyAndSave() has already run (called from OnFormClosing).
        _nodeItem.Text = $"Node: {_cfg.NodeName}";

        bool urlChanged = _cfg.PrismUrl != prevUrl || _cfg.NodeName != prevNode;
        if (urlChanged)
        {
            MessageBox.Show(
                "Server URL or Node Name changed.\n\nRestart PRISM Agent to apply the new connection settings.",
                "Settings Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            // Just re-announce the (possibly updated) config to the server.
            SendHello();
        }
    }

    void ShowLogs()
    {
        _logsForm ??= new LogsForm(_logProvider);
        _logsForm.Show();
        _logsForm.BringToFront();
    }

    void OnCheckUpdate(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            Updater.UpdateInfo? info = null;
            try   { info = await Updater.CheckForUpdateAsync(); }
            catch (Exception ex)
            {
                _sync.BeginInvoke(() =>
                    MessageBox.Show($"Update check failed:\n{ex.Message}",
                        "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return;
            }

            var captured = info; // explicit capture to satisfy nullable flow in the delegate
            _sync.BeginInvoke(() =>
            {
                if (captured is null)
                {
                    MessageBox.Show("You are running the latest version.",
                        "No Updates Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var res = MessageBox.Show(
                    $"Update available: {captured.TagName}\n\nDownload and install now?\n\n" +
                    "(The agent will restart automatically.)",
                    "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (res == DialogResult.Yes)
                    _ = InstallUpdateAsync(captured);
            });
        });
    }

    async Task InstallUpdateAsync(Updater.UpdateInfo info)
    {
        var prog = new Progress<int>(p =>
            _sync.BeginInvoke(() => _tray.Text = $"PRISM Agent — Downloading {p}%"));
        try
        {
            await Updater.DownloadAndInstallAsync(info, prog);
        }
        catch (Exception ex)
        {
            _sync.BeginInvoke(() =>
                MessageBox.Show($"Update failed:\n{ex.Message}",
                    "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
    }

    void OnToggleAgent(object? sender, EventArgs e)
    {
        if (_agentRunning)
        {
            _agentRunning       = false;
            _toggleItem.Text    = "▶  Start Agent";
            ApplyState(TrayState.Stopped);
            _ = _ws.PauseAsync();
        }
        else
        {
            _agentRunning       = true;
            _toggleItem.Text    = "■  Stop Agent";
            ApplyState(TrayState.Connecting);
            _ws.Resume();
        }
    }

    async void OnExit(object? sender, EventArgs e)
    {
        _tray.Visible = false;
        try { await _host.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        Application.Exit();
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _sync.Dispose();
            _logsForm?.Dispose();
            _settingsForm?.Dispose();
        }
        base.Dispose(disposing);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    void Add(ToolStripItem item) => _menu.Items.Add(item);

    ToolStripMenuItem MakeRoleItem(string label, AgentRole role)
    {
        var item = new ToolStripMenuItem(label)
        {
            CheckOnClick = true,
            Checked      = _cfg.Roles.Contains(role),
        };
        item.Click += (_, _) => UpdateRoles();
        return item;
    }

    /// <summary>Draws a solid coloured circle of 16 × 16 px and returns it as an <see cref="Icon"/>.</summary>
    static Icon MakeCircleIcon(Color fill)
    {
        const int sz = 16;
        var bmp = new Bitmap(sz, sz, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Subtle dark ring
            using (var ring = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(ring, 0, 0, sz - 1, sz - 1);
            // Coloured fill
            using (var inner = new SolidBrush(fill))
                g.FillEllipse(inner, 1, 1, sz - 3, sz - 3);
            // Small highlight
            using (var hi = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
                g.FillEllipse(hi, 3, 2, 5, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
