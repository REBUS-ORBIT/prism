using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Auth;

/// <summary>
/// Reads the ORBIT bearer token from process environment variables.
/// Two variables, one per environment, matching the convention every
/// other ORBIT-side service in the monorepo uses:
///
///   <c>ORBIT_PAT_PROD</c> — token for <c>https://orbit.rebus.industries</c>
///   <c>ORBIT_PAT_DEV</c>  — token for <c>https://orbit-dev.rebus.industries</c>
///
/// Returns <c>null</c> (rather than throwing) on missing / empty so
/// the composite chain can fall through to the on-disk token store
/// without surfacing a confusing error to the agent.
/// </summary>
public sealed class EnvOrbitTokenSource : IOrbitTokenSource
{
    private readonly Func<string, string?> _readEnv;

    public EnvOrbitTokenSource()
        : this(Environment.GetEnvironmentVariable) { }

    /// <summary>Test seam: inject a non-process env reader.</summary>
    public EnvOrbitTokenSource(Func<string, string?> readEnv)
    {
        _readEnv = readEnv ?? throw new ArgumentNullException(nameof(readEnv));
    }

    public Task<string?> GetTokenAsync(ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var key = server.Name switch
        {
            "prod" => "ORBIT_PAT_PROD",
            "dev" => "ORBIT_PAT_DEV",
            _ => null,
        };
        if (key is null) return Task.FromResult<string?>(null);

        var raw = _readEnv(key);
        var token = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        return Task.FromResult(token);
    }
}
