// PRISM.Agent — entrypoint.
//
// Runs as a WinForms system-tray process by default (tray mode).
// Pass --headless to fall back to the classic background-service behaviour
// (e.g. from Task Scheduler with --headless, or CI pipelines).

using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Config;
using PRISM.Agent.Pipeline;
using PRISM.Agent.Rhino;
using PRISM.Agent.Tray;
using PRISM.Agent.Ws;

namespace PRISM.Agent;

public static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        bool headless = !Environment.UserInteractive || args.Contains("--headless");

        // Enable visual styles before any control handle is created.
        if (!headless)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        // First positional arg (non-flag) is treated as the config file path.
        var configPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var cfg = AgentConfig.Load(configPath);

        // Probe for the requested Rhino version and hook the Rhino.Inside assembly
        // resolver BEFORE the host is built and before any Rhino.* types are accessed.
        using var preHostLog = LoggerFactory.Create(b => b.AddConsole());
        var rhinoSelector = new RhinoVersionSelector(
            preHostLog.CreateLogger<RhinoVersionSelector>());
        try
        {
            rhinoSelector.Initialize(cfg.RhinoVersion);
        }
        catch (InvalidOperationException ex)
        {
            if (headless)
            {
                Console.Error.WriteLine($"[PRISM.Agent] FATAL: {ex.Message}");
                return;
            }
            // In tray mode continue — the tray will show the error state and the
            // user can install Rhino then restart the agent.
        }

        var builder = Host.CreateApplicationBuilder(
            args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray());

        // In tray mode inject the in-process log provider so LogsForm can show output.
        TrayLoggerProvider? trayLogger = null;
        if (!headless)
            trayLogger = new TrayLoggerProvider();

        builder.Logging
            .ClearProviders()
            .AddConsole();

        if (OperatingSystem.IsWindows())
            builder.Logging.AddEventLog(s => s.SourceName = "PRISM.Agent");

        if (trayLogger != null)
            builder.Logging.AddProvider(trayLogger);

        builder.Services.AddSingleton(cfg);
        builder.Services.AddSingleton(rhinoSelector);

        builder.Services.AddSingleton(sp =>
            new WsClient(new Uri(cfg.PrismUrl), sp.GetRequiredService<ILogger<WsClient>>()));

        builder.Services.AddSingleton<RhinoHost>(sp => new RhinoHost(
            sp.GetRequiredService<ILogger<RhinoHost>>(),
            rhinoSelector.SelectedSystemDir ?? cfg.RhinoExecutablePath));

        builder.Services.AddSingleton<RhinoFileOpener>();
        builder.Services.AddTransient<ConvertJob>();
        builder.Services.AddTransient<PollLayersJob>();
        builder.Services.AddSingleton<WorkerSlotPool>(sp => new WorkerSlotPool(
            sp.GetRequiredService<ILogger<WorkerSlotPool>>(),
            () => sp.GetRequiredService<ConvertJob>(),
            () => sp.GetRequiredService<PollLayersJob>(),
            sp.GetRequiredService<WsClient>(),
            cfg.Slots));

        builder.Services.AddSingleton<AgentMessageDispatcher>();

        // Only add the heartbeat/hello service in headless mode; the tray context
        // manages the WS lifecycle directly (starts the host after UI is ready).
        builder.Services.AddHostedService<AgentService>();

        var host = builder.Build();

        // Force dispatcher + slot pool to materialise so their event subscriptions run.
        _ = host.Services.GetRequiredService<WorkerSlotPool>();
        _ = host.Services.GetRequiredService<AgentMessageDispatcher>();

        if (headless)
        {
            await host.RunAsync();
        }
        else
        {
            Application.Run(new PrismTrayContext(host, cfg, trayLogger!));
        }
    }
}
