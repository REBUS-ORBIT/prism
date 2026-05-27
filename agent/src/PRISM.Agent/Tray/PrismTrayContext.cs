using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PRISM.Agent;
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
    readonly AgentControlPlane   _plane;
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
    LogsForm?            _logsForm;
    SettingsForm?        _settingsForm;
    UpdateProgressForm?  _updateForm;

    // Used to marshal callbacks from WsClient (ThreadPool) → WinForms message loop.
    readonly Control _sync = new Panel();

    bool _agentRunning = true;

    // -----------------------------------------------------------------------
    // Pre-built tray icons
    //
    // v0.1.35: the tray icon is now the PRISM logo loaded from the multi-
    // resolution PRISM.Agent.ico shipped next to the executable. Status is
    // still discoverable through the tray tooltip ("PRISM Agent — Connected
    // /Connecting…/Stopped") and the disabled "Status: …" menu item. The
    // legacy coloured circle (used for v0.1.34 and earlier) is retained as
    // a fall-back when the .ico file cannot be found on disk so the agent
    // never starts with a blank/empty tray.
    // -----------------------------------------------------------------------
    static readonly Icon _logoIcon = LoadLogoIcon();

    // -----------------------------------------------------------------------

    public PrismTrayContext(IHost host, AgentConfig cfg, TrayLoggerProvider logProvider)
    {
        _host        = host;
        _cfg         = cfg;
        _logProvider = logProvider;
        _ws          = host.Services.GetRequiredService<WsClient>();
        _plane       = host.Services.GetRequiredService<AgentControlPlane>();

        // Ensure the dummy control has a Windows handle so BeginInvoke works
        // from background threads before the first real window is created.
        _sync.CreateControl();

        // ---- Context menu ----
        // ShowCheckMargin=true is REQUIRED so the checkable role items render
        // a visible checkmark glyph; with ShowImageMargin=false the check would
        // have nowhere to draw (Windows hides the indicator silently).
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };
        _menu.Opening += (_, _) => RefreshRoleCheckmarks();

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

        _convItem = MakeRoleItem("Conversion (run .3dm/.dwg/.fbx conversions)", AgentRole.Conversion);
        _layItem  = MakeRoleItem("Layering (return layer info)",                AgentRole.Layering);
        _rcvItem  = MakeRoleItem("Receive (download .3dm back from ORBIT)",     AgentRole.Receive);
        Add(_convItem);
        Add(_layItem);
        Add(_rcvItem);
        Add(new ToolStripSeparator());

        Add(new ToolStripMenuItem("⚙  Settings…",         null, OnSettings));
        Add(new ToolStripMenuItem("🌐  Open Web UI",       null, (_, _) => OpenWebUi()));
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
            Icon             = _logoIcon,
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

        // ---- Web UI state changes (pause/resume, slot/role updates) ----
        _plane.StateChanged += () => _sync.BeginInvoke(SyncFromPlane);

        // ---- Start the host (non-blocking) ----
        _ = _host.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _sync.BeginInvoke(() =>
                    MessageBox.Show($"Host failed to start:\n{t.Exception?.InnerException?.Message}",
                        "PRISM Agent Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
        });

        // ---- Post-update "we just upgraded" balloon ----
        // Fires once per actual upgrade.  Reads + deletes the marker
        // file written by Updater.DownloadAndInstallAsync; the
        // version-match check inside ConsumeLastUpdateSuccess() means
        // we only celebrate when the new assembly is actually loaded.
        TryShowPostUpdateBalloon();
    }

    /// <summary>
    /// Fires a tray balloon shortly after startup if the previous run
    /// successfully updated the agent.  Reads + deletes the
    /// <c>%TEMP%\PRISM.Agent.Update.NewVersion</c> marker so the
    /// balloon only appears once per upgrade.
    /// </summary>
    void TryShowPostUpdateBalloon()
    {
        try
        {
            var upgradedTag = Updater.ConsumeLastUpdateSuccess();
            if (upgradedTag is null) return;

            var currentVersion =
                typeof(PrismTrayContext).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            // Defer the balloon slightly so the NotifyIcon is fully
            // realised in the shell (early ShowBalloonTip calls can
            // silently drop on slow boot).
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                try
                {
                    _sync.BeginInvoke(() =>
                    {
                        if (_tray.Visible)
                        {
                            _tray.BalloonTipIcon  = ToolTipIcon.Info;
                            _tray.BalloonTipTitle = "PRISM Agent updated";
                            _tray.BalloonTipText  = $"Now running v{currentVersion} ({upgradedTag}).";
                            _tray.ShowBalloonTip(8000);
                        }
                    });
                }
                catch { /* shell may have not surfaced the icon yet — non-fatal */ }
            });
        }
        catch { /* defensive — never crash the tray init for a balloon */ }
    }

    // -----------------------------------------------------------------------
    // State updates
    // -----------------------------------------------------------------------

    enum TrayState { Connected, Connecting, Stopped }

    void ApplyState(TrayState state)
    {
        // The tray icon itself is the PRISM logo at every state; status is
        // surfaced via the tooltip and the disabled "Status: …" menu item.
        _tray.Icon = _logoIcon;
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
        _ = _plane.SetSlotsAsync(next);
    }

    void UpdateRoles()
    {
        var roles = new List<AgentRole>();
        if (_convItem.Checked) roles.Add(AgentRole.Conversion);
        if (_layItem.Checked)  roles.Add(AgentRole.Layering);
        if (_rcvItem.Checked)  roles.Add(AgentRole.Receive);
        _ = _plane.SetRolesAsync(roles.ToArray());
    }

    /// <summary>
    /// Re-render tray bits that depend on <see cref="AgentControlPlane"/>
    /// state (slot count, role checkmarks, pause label) — called whenever
    /// the web UI mutates settings.
    /// </summary>
    void SyncFromPlane()
    {
        _slotsItem.Text  = $"Workers: {_cfg.Slots}";
        _nodeItem.Text   = $"Node: {_cfg.NodeName}";
        RefreshRoleCheckmarks();

        // Mirror watcher pause state into the tray toggle so the
        // ■ Stop / ▶ Start label and tray icon stay accurate.
        if (_plane.IsPaused && _agentRunning)
        {
            _agentRunning    = false;
            _toggleItem.Text = "▶  Start Agent";
            ApplyState(TrayState.Stopped);
        }
        else if (!_plane.IsPaused && !_agentRunning)
        {
            _agentRunning    = true;
            _toggleItem.Text = "■  Stop Agent";
            ApplyState(_ws.IsConnected ? TrayState.Connected : TrayState.Connecting);
        }
    }

    /// <summary>
    /// Re-syncs each role item's <see cref="ToolStripMenuItem.Checked"/> flag
    /// from <see cref="AgentConfig.Roles"/>. Wired to <c>ContextMenuStrip.Opening</c>
    /// so the menu always reflects the latest config — even when roles were
    /// edited elsewhere (e.g. SettingsForm or the on-disk JSON).
    /// </summary>
    void RefreshRoleCheckmarks()
    {
        _convItem.Checked = _cfg.Roles.Contains(AgentRole.Conversion);
        _layItem.Checked  = _cfg.Roles.Contains(AgentRole.Layering);
        _rcvItem.Checked  = _cfg.Roles.Contains(AgentRole.Receive);
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
            _ = _plane.SendHelloAsync();
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

                // v0.1.34: replace the bare Yes/No MessageBox with a
                // richer dialog that surfaces the new tag, download
                // size, and a preview of the release notes so the
                // operator can decide informedly.
                var currentVersion =
                    typeof(PrismTrayContext).Assembly.GetName().Version?.ToString() ?? "0.0.0";

                using var dlg = new UpdateAvailableDialog(captured, currentVersion);
                var res = dlg.ShowDialog();
                if (res == DialogResult.OK)
                    _ = InstallUpdateAsync(captured);
            });
        });
    }

    async Task InstallUpdateAsync(Updater.UpdateInfo info)
    {
        // Spin up (or recycle) the progress form on the UI thread first
        // so its handle is created before the worker thread starts
        // poking it.
        UpdateProgressForm? form = null;
        try
        {
            _sync.Invoke(() =>
            {
                _updateForm?.Dispose();
                _updateForm = new UpdateProgressForm(info.TagName);
                _updateForm.Show();
                _updateForm.BringToFront();
                form = _updateForm;
            });
        }
        catch
        {
            // If the form can't be created (rare — e.g. tray UI was
            // suppressed in session 0), fall through and run the
            // updater without UI.  The visible PowerShell console
            // window still gives the operator feedback.
            form = null;
        }

        var prog = new Progress<int>(p =>
        {
            form?.SetDownloadProgress(p);
            _sync.BeginInvoke(() => _tray.Text = $"PRISM Agent — Downloading {p}%");
        });

        try
        {
            await Updater.DownloadAndInstallAsync(info, prog);

            // DownloadAndInstallAsync calls Application.Exit() at the
            // very end; the message-loop teardown will dispose the form
            // for us.  Until that fires, flip the form into the
            // "installing" state so the user sees the handoff to
            // PowerShell.
            form?.SetInstalling();
        }
        catch (Exception ex)
        {
            form?.SetFailed(ex.Message);
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
            _ = _plane.PauseAsync();
        }
        else
        {
            _agentRunning       = true;
            _toggleItem.Text    = "■  Stop Agent";
            ApplyState(TrayState.Connecting);
            _plane.Resume();
        }
    }

    void OpenWebUi()
    {
        if (_cfg.WebUiPort <= 0)
        {
            MessageBox.Show(
                "The local web UI is disabled (webUiPort = 0).\n\n"
                + "Edit agent-config.json to set a port and restart the agent.",
                "PRISM Agent",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var url = $"http://localhost:{_cfg.WebUiPort}/";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to open {url}:\n\n{ex.Message}",
                "PRISM Agent",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            _updateForm?.Dispose();
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

    /// <summary>
    /// Loads <c>Assets/PRISM.Agent.ico</c> from the publish output. The
    /// multi-resolution .ico (16/32/48/64/128/256) ships next to the EXE
    /// via the csproj <c>&lt;Content Include="Assets/PRISM.Agent.ico"&gt;</c>.
    /// Falls back to the legacy amber circle if the file is missing or
    /// fails to decode — the tray must never come up without an icon.
    /// </summary>
    static Icon LoadLogoIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PRISM.Agent.ico");
            if (File.Exists(path))
                return new Icon(path);
        }
        catch { /* fall through to placeholder */ }
        return MakeCircleIcon(Color.FromArgb(255, 152, 0));
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
