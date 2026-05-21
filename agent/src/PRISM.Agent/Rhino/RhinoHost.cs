using Microsoft.Extensions.Logging;
using Rhino.Runtime.InProcess;

namespace PRISM.Agent.Rhino;

/// <summary>
/// Process-wide Rhino host wrapper.
///
/// Owns the single <see cref="RhinoCore"/> instance that starts the Rhino 8
/// engine inside this process.  Must be constructed AFTER
/// <see cref="RhinoInside.Resolver.Initialize"/> has been called so that
/// managed + native Rhino assemblies can be resolved.
///
/// Per-job worker slots call <see cref="CreateDoc"/> to get a fresh headless
/// document; all serialise through the single Rhino engine (Rhino is not
/// re-entrant).
/// </summary>
public sealed class RhinoHost : IDisposable
{
    readonly ILogger<RhinoHost> _log;
    readonly RhinoCore _core;
    bool _disposed;

    public RhinoHost(ILogger<RhinoHost> log, string? rhinoSystemDir = null)
    {
        _log = log;

        // Prepend the Rhino system directory to PATH so that the Windows DLL
        // loader finds native Rhino DLLs via the default search order.  This
        // complements the AddDllDirectory call made by Resolver.Initialize and
        // ensures DllImport attributes that use LoadLibrary (not LoadLibraryEx
        // with LOAD_LIBRARY_SEARCH_USER_DIRS) also succeed.
        if (!string.IsNullOrEmpty(rhinoSystemDir))
            PrependToPath(rhinoSystemDir);

        _log.LogInformation("RhinoHost: starting Rhino.Inside core{Suffix}",
            rhinoSystemDir is null ? "" : $" — system dir: {rhinoSystemDir}");

        // RhinoCore is the object that actually loads the native Rhino engine
        // DLLs and initialises the Rhino application object.  Without it,
        // any P/Invoke into the Rhino C++ layer throws DllNotFoundException.
        _core = new RhinoCore(new[] { "/nosplash", "/notemplate" });

        _log.LogInformation("RhinoHost: Rhino {Version} ready", global::Rhino.RhinoApp.Version);

        // The RDK (Renderer Development Kit) plug-in owns
        // `RenderMaterial.FindRenderTexture`, `RenderTexture.SimulatedTexture`,
        // and the entire `Rhino.Render.*` material/texture API. In a headless
        // Rhino.Inside host RDK is NOT auto-loaded — meaning every texture
        // extraction strategy in the connector silently returns null, which
        // is the primary suspect for PRISM uploads landing with zero blobs.
        // Force-load it here and log the result so we can confirm in v0.1.14
        // diagnostics whether the RDK was the blocker.
        EnsureRdkLoaded();
    }

    /// <summary>
    /// Canonical RDK plug-in GUID — the same value Rhino uses to identify
    /// the RhinoRdk.rhp shipped in every Rhino 8 install.
    /// </summary>
    static readonly Guid RdkPlugInId = new("16592D58-4A2F-401D-BF5E-3B87741C1B1B");

    /// <summary>
    /// Snapshot of the RDK probe results captured during <see cref="EnsureRdkLoaded"/>.
    /// <see cref="Pipeline.ConvertJob"/> reads this at the start of each job
    /// so the line surfaces in the admin UI alongside the conversion logs —
    /// the host startup log itself only lives in the agent's local Serilog
    /// file, which is hard to read from PRISM admin.
    /// </summary>
    public static string? LastRdkReport { get; private set; }

    void EnsureRdkLoaded()
    {
        // Step 1: log the on-disk path Rhino has registered for the RDK GUID
        // (proves the plug-in is at least installed and discoverable). A null
        // / empty path means "not registered in this Rhino installation",
        // which is itself useful diagnostic information.
        string? rdkPath = TryGet(() => global::Rhino.PlugIns.PlugIn.PathFromId(RdkPlugInId));
        _log.LogWarning(
            "RhinoHost: RDK PathFromId({Id}) → '{Path}'",
            RdkPlugInId, rdkPath ?? "<unknown>");

        // Step 2: force-load. Returns false when the plug-in is missing OR
        // when it is already loaded (depending on Rhino version) — log it
        // either way so we can correlate against the probe result below.
        string loadResult;
        try
        {
            var ok = global::Rhino.PlugIns.PlugIn.LoadPlugIn(RdkPlugInId);
            loadResult = ok.ToString();
        }
        catch (Exception err)
        {
            loadResult = $"<threw {err.GetType().Name}: {err.Message}>";
        }
        _log.LogWarning("RhinoHost: PlugIn.LoadPlugIn(RDK) → {Result}", loadResult);

        // Step 3: exercise an RDK code path. We can't reliably call into
        // the document material collection here (ActiveDoc may legitimately
        // be null in the headless host before the first job opens a file),
        // but we CAN ask the RDK type system how many render-content types
        // it has registered. >0 means RDK is alive; 0 / throw means RDK has
        // not initialised and texture extraction will silently return null
        // throughout the connector pipeline.
        int registeredTypeCount = -1;
        string? probeError = null;
        try
        {
            var registeredTypes = global::Rhino.Render.RenderContentType.GetAllAvailableTypes();
            registeredTypeCount = registeredTypes?.Length ?? 0;
            _log.LogWarning(
                "RhinoHost: RDK probe — RenderContentType.GetAllAvailableTypes() returned {Total} types. " +
                ">0 means RDK is live and SimulatedTexture / FindRenderTexture will work.",
                registeredTypeCount);
        }
        catch (Exception probeErr)
        {
            probeError = $"{probeErr.GetType().Name}: {probeErr.Message}";
            _log.LogWarning(probeErr,
                "RhinoHost: RDK probe (RenderContentType.GetAllAvailableTypes) threw — " +
                "RDK is NOT functional in this host. Texture extraction will fail.");
        }

        // Stash a single-line summary so each job can re-emit it through the
        // WS log channel. Format keeps it grep-able and short.
        LastRdkReport =
            $"RDK PathFromId='{rdkPath ?? "<unknown>"}' LoadPlugIn={loadResult} " +
            (probeError is null
                ? $"RenderContentTypes={registeredTypeCount}"
                : $"probeThrew={probeError}");
    }

    public string RhinoVersion =>
        TryGet(() => global::Rhino.RhinoApp.Version.ToString()) ?? "unknown";

    /// <summary>Create a fresh headless RhinoDoc for a job to populate.</summary>
    public global::Rhino.RhinoDoc CreateDoc() =>
        global::Rhino.RhinoDoc.CreateHeadless(null)
        ?? throw new InvalidOperationException("RhinoDoc.CreateHeadless returned null — is Rhino 8 installed?");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.Dispose();
    }

    static void PrependToPath(string dir)
    {
        const string name = "PATH";
        var current = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) ?? string.Empty;
        var already = current.Split(';').Any(d =>
            d.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!already)
            Environment.SetEnvironmentVariable(name, dir + ";" + current, EnvironmentVariableTarget.Process);
    }

    static T? TryGet<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }
}
