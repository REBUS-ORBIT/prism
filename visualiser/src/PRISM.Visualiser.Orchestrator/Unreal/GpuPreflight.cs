using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

using Serilog;

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Phase K hardening: a fast pre-flight check that runs before
/// <see cref="Pipeline.VisualiserPipeline.ReceiveAndStageAsync"/> /
/// <c>ImportAsync</c> and refuses to start when the workstation
/// obviously can't host a Pixel Streaming session.
///
/// <para>
/// Two checks are performed:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <c>nvidia-smi</c> is on <c>PATH</c> AND reports at least
///       <c>MinFreeVramGb</c> GB free VRAM on the first listed GPU.
///       The default minimum is 4&#160;GB which empirically lets
///       UE 5.7 + Pixel Streaming 2 boot with room to spare for the
///       NVENC encoder.
///     </description>
///   </item>
///   <item>
///     <description>
///       No <c>UnrealEditor*.exe</c> processes are already running.
///       UE editor instances compete for the GPU encoder; a stray
///       editor from a prior crashed run will cause Pixel Streaming
///       to silently fall back to software encode (~12&#160;fps), which
///       looks like a network problem to the user.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>Soft / hard mode.</b> If <c>nvidia-smi</c> is not on <c>PATH</c>
/// the workstation may genuinely be running on integrated graphics or
/// an AMD GPU. PS2 can fall back to software encode there (at reduced
/// FPS), so the default behaviour is to log a warning and continue. Set
/// <see cref="Strict"/> = <c>true</c> (CLI flag <c>--strict-gpu</c>) on
/// production-grade workstations to flip this into a hard rejection.
/// </para>
///
/// <para>
/// <b>Wiring.</b> The pipeline call site looks like:
/// <code>
///   var preflight = new GpuPreflight(_log, strict: cli.StrictGpu);
///   var pre = preflight.Check();
///   if (!pre.Ok)
///   {
///       var failed = new FailedEvent(
///           Schema: "prism-visualiser/failed/v1",
///           RunId:  manifest.RunId,
///           Error:  "visualisation_failed",
///           Code:   GpuPreflight.FailureCode,
///           Message: pre.Reason ?? "GPU pre-flight failed");
///       StructuredLog.Emit(failed);
///       return GpuPreflight.ExitCode;  // 10
///   }
/// </code>
/// Lives in <c>Unreal/</c> rather than <c>Process/</c> because the
/// rationale (NVENC, UE editor processes) is UE-specific and the file
/// sits next to <see cref="UnrealEnvironment"/> which also probes the
/// host before launching.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuPreflight
{
    /// <summary>
    /// Stable failure code the orchestrator emits in the
    /// <c>prism-visualiser/failed/v1</c> envelope when this pre-flight
    /// rejects a run. Documented in
    /// <c>server/src/docs/openapi.ts</c> as part of the
    /// <c>VisualiserFailedResponse.code</c> enum.
    /// </summary>
    public const string FailureCode = "gpu_preflight_failed";

    /// <summary>
    /// Conventional process exit code on pre-flight failure. The agent
    /// distinguishes pre-flight rejections (recoverable on a different
    /// workstation) from generic orchestrator crashes (escalate).
    /// </summary>
    public const int ExitCode = 10;

    /// <summary>Default minimum free VRAM. 4&#160;GB tracks UE 5.7 + PS2 + NVENC + headroom.</summary>
    public const double DefaultMinFreeVramGb = 4.0;

    /// <summary>
    /// Default process-name prefix that triggers the "stale UE editor
    /// already running" branch. Glob-like: matches any process whose
    /// name starts with <c>UnrealEditor</c> (covers
    /// <c>UnrealEditor.exe</c>, <c>UnrealEditor-Cmd.exe</c>, and the
    /// <c>UnrealEditor-Linux*</c> variants on the rare cross-platform
    /// host).
    /// </summary>
    public const string DefaultEditorProcessPrefix = "UnrealEditor";

    private readonly ILogger _log;
    private readonly IGpuProbe _probe;

    /// <summary>
    /// Hard-rejects on missing <c>nvidia-smi</c>. Off by default
    /// because the orchestrator runs in mixed fleets (some
    /// integrated-GPU dev boxes). CI sets it via <c>--strict-gpu</c>.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Minimum free VRAM in GB; runs below this fail pre-flight.</summary>
    public double MinFreeVramGb { get; init; } = DefaultMinFreeVramGb;

    /// <summary>Process-name prefix considered "stale UE editor". See <see cref="DefaultEditorProcessPrefix"/>.</summary>
    public string EditorProcessPrefix { get; init; } = DefaultEditorProcessPrefix;

    /// <summary>Outcome record. <c>Ok=true</c> means the pipeline may proceed; <c>Reason</c> is human-readable when <c>Ok=false</c>; <c>FreeVramGb</c> is populated whenever <c>nvidia-smi</c> succeeded.</summary>
    public sealed record Result(bool Ok, string? Reason, double? FreeVramGb);

    /// <summary>
    /// Production constructor: shells out to <c>nvidia-smi</c> and
    /// enumerates processes via <see cref="Process.GetProcessesByName(string)"/>.
    /// </summary>
    public GpuPreflight(ILogger log, bool strict = false)
        : this(log, new RealGpuProbe(), strict) { }

    /// <summary>Test-seam constructor. Inject a fake <see cref="IGpuProbe"/>.</summary>
    public GpuPreflight(ILogger log, IGpuProbe probe, bool strict = false)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        Strict = strict;
    }

    /// <summary>Run both checks and return a <see cref="Result"/>.</summary>
    public Result Check()
    {
        // ---------------------------------------------------------- VRAM
        var vram = _probe.TryGetFirstGpuFreeVramGb();
        if (vram is null)
        {
            var msg = "nvidia-smi is not available on PATH or returned no GPUs";
            if (Strict)
            {
                _log.Error("gpu-preflight: {Msg} (strict mode = reject)", msg);
                return new Result(false, msg, null);
            }
            _log.Warning("gpu-preflight: {Msg} (soft mode = continue; PS2 will fall back to software encode)", msg);
        }
        else if (vram.Value < MinFreeVramGb)
        {
            var msg = $"insufficient free VRAM: {vram.Value:0.00} GB < {MinFreeVramGb:0.00} GB minimum";
            _log.Error("gpu-preflight: {Msg}", msg);
            return new Result(false, msg, vram);
        }
        else
        {
            _log.Information("gpu-preflight: vram-ok free={Free:0.00}GB min={Min:0.00}GB",
                vram.Value, MinFreeVramGb);
        }

        // ----------------------------------------------------- stale UE
        var existing = _probe.GetRunningEditorProcessNames(EditorProcessPrefix);
        if (existing.Count > 0)
        {
            var msg = $"{existing.Count} stale Unreal editor process(es) detected: {string.Join(", ", existing)} — refuse to launch (they would clash for the NVENC encoder)";
            _log.Error("gpu-preflight: {Msg}", msg);
            return new Result(false, msg, vram);
        }
        _log.Information("gpu-preflight: no stale Unreal editor processes");

        return new Result(true, null, vram);
    }

    // ------------------------------------------------------------------
    // Probe abstraction
    // ------------------------------------------------------------------

    /// <summary>Test seam over the two host calls the pre-flight performs.</summary>
    public interface IGpuProbe
    {
        /// <summary>
        /// Shell out to <c>nvidia-smi --query-gpu=memory.free
        /// --format=csv,noheader,nounits</c> and return the first GPU's
        /// free VRAM in GB. <c>null</c> means nvidia-smi is missing or
        /// returned no rows.
        /// </summary>
        double? TryGetFirstGpuFreeVramGb();

        /// <summary>
        /// Names (without the <c>.exe</c> suffix) of all processes
        /// whose <see cref="Process.ProcessName"/> starts with
        /// <paramref name="namePrefix"/>. Empty list when nothing
        /// matches.
        /// </summary>
        IReadOnlyList<string> GetRunningEditorProcessNames(string namePrefix);
    }

    /// <summary>Real implementation backed by <see cref="Process"/>.</summary>
    private sealed class RealGpuProbe : IGpuProbe
    {
        public double? TryGetFirstGpuFreeVramGb()
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=memory.free --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) return null;
                var stdout = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(10_000)) return null;
                if (proc.ExitCode != 0) return null;
                return ParseFirstGpuVramMb(stdout) is { } mb
                    ? mb / 1024.0
                    : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // nvidia-smi not on PATH.
                return null;
            }
            catch (Exception)
            {
                // Any other failure (timeout, parse) is treated as
                // "unknown" — falls into the soft/strict branch above.
                return null;
            }
        }

        public IReadOnlyList<string> GetRunningEditorProcessNames(string namePrefix)
        {
            var hits = new List<string>();
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(p.ProcessName);
                    }
                }
                catch
                {
                    // Process may have exited between GetProcesses() and
                    // the ProcessName read — ignore.
                }
                finally
                {
                    p.Dispose();
                }
            }
            return hits;
        }
    }

    // ------------------------------------------------------------------
    // Parsing — public so tests can verify directly.
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse the first line of <c>nvidia-smi</c>'s
    /// <c>--query-gpu=memory.free --format=csv,noheader,nounits</c>
    /// output. Returns the free VRAM in MB, or <c>null</c> if the
    /// output is empty / not numeric.
    /// </summary>
    public static int? ParseFirstGpuVramMb(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        using var reader = new StringReader(stdout);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            // Trim any trailing units string defensively (the
            // `--format=...,nounits` flag should remove them, but
            // some driver builds still emit `2048 MiB`).
            var sb = new StringBuilder();
            foreach (var c in trimmed)
            {
                if (c is >= '0' and <= '9') sb.Append(c);
                else if (c == ',' || c == '.') break;
                else if (sb.Length > 0) break;
            }
            if (sb.Length > 0 && int.TryParse(sb.ToString(), out var mb))
            {
                return mb;
            }
            return null;
        }
        return null;
    }
}
