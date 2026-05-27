using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Config;
using PRISM.Agent.Pipeline;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent;

/// <summary>
/// Main hosted service. Wires the WS client, sends <c>hello</c> on
/// connect, runs the heartbeat loop, and surfaces dispatcher acks.
/// </summary>
public sealed class AgentService : BackgroundService
{
    readonly ILogger<AgentService> _log;
    readonly AgentConfig _cfg;
    readonly WsClient _ws;
    readonly AgentMessageDispatcher _dispatcher;
    readonly WorkerSlotPool _slots;

    public AgentService(
        ILogger<AgentService> log,
        AgentConfig cfg,
        WsClient ws,
        AgentMessageDispatcher dispatcher,
        WorkerSlotPool slots)
    {
        _log = log;
        _cfg = cfg;
        _ws = ws;
        _dispatcher = dispatcher;
        _slots = slots;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("PRISM.Agent starting: node={NodeName} machineId={MachineId} slots={Slots} prismUrl={Url}",
            _cfg.NodeName, _cfg.MachineId, _cfg.Slots, _cfg.PrismUrl);

        // If the previous run attempted an in-app update and the script
        // logged a failure, surface that prominently so the operator
        // doesn't think "Check for updates" was a no-op.
        var lastFailure = Tray.Updater.GetLastUpdateFailure();
        if (lastFailure is not null)
        {
            _log.LogError(
                "Previous in-app update attempt failed. Diagnostic log:\n{Log}",
                lastFailure);
        }

        // Visualiser role pre-flight: if Visualiser is enabled but UE is
        // not where the operator said it would be, warn loudly. The agent
        // keeps running so other roles still work — Phase G's dispatcher
        // only routes runs to agents whose `canVisualise` is on, so a
        // misconfigured box will just sit idle until either the admin
        // turns the role off or the operator installs UE.
        if (_cfg.Roles.Contains(AgentRole.Visualiser))
        {
            var ueRoot = _cfg.UnrealEngineRoot ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ueRoot) || !Directory.Exists(ueRoot))
            {
                _log.LogWarning(
                    "Visualiser role enabled but UE root not found: {UnrealEngineRoot}",
                    ueRoot);
            }
            else
            {
                _log.LogInformation("Visualiser role enabled; UE root {UnrealEngineRoot} found", ueRoot);
            }
        }

        _ws.OnReconnected += SendHelloFireAndForget;
        await _ws.StartAsync(stoppingToken);
        SendHelloFireAndForget();

        // Heartbeat loop
        var hb = TimeSpan.FromSeconds(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(hb, stoppingToken); }
            catch (TaskCanceledException) { break; }

            try
            {
                await _ws.SendAsync(MessageType.Heartbeat, new HeartbeatData
                {
                    SlotsBusy = _slots.BusyCount,
                });
            }
            catch (Exception err)
            {
                _log.LogWarning(err, "heartbeat send failed");
            }
        }

        _log.LogInformation("PRISM.Agent stopping");
    }

    void SendHelloFireAndForget()
    {
        var hello = new HelloData
        {
            MachineId = _cfg.MachineId,
            NodeName = _cfg.NodeName,
            Slots = _cfg.Slots,
            Formats = SupportedFormats,
            Roles = _cfg.Roles,
            AgentVersion = typeof(AgentService).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            RhinoVersion = null,  // Phase 3: read from Rhino.Inside host
        };
        _ = _ws.SendAsync(MessageType.Hello, hello);
    }

    internal static readonly string[] SupportedFormats =
    {
        // Phase 2 scaffold reports what Rhino *can* handle once Phase 3
        // wires up the importers. The orchestrator uses this list to
        // route jobs. `.zip` is the bundle ingestion format — the agent
        // extracts it via ZipBundleExtractor and hands the primary
        // geometry file to RhinoFileOpener at job runtime.
        ".3dm", ".dwg", ".dxf", ".fbx", ".obj", ".stl", ".ply",
        ".3mf", ".dae", ".step", ".stp", ".iges", ".igs", ".zip",
    };
}
