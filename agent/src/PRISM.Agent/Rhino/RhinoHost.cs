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

        _log.LogInformation("RhinoHost: Rhino {Version} ready", RhinoApp.Version);
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
