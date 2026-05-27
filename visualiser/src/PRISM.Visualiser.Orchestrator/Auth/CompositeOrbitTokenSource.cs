using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Auth;

/// <summary>
/// Chains multiple <see cref="IOrbitTokenSource"/>s and returns the
/// first non-null token. Default chain:
///
///   1. <see cref="EnvOrbitTokenSource"/> — env vars (developer mode)
///   2. <see cref="FileOrbitTokenSource"/> — <c>%LOCALAPPDATA%</c> store
///      written by the PRISM Agent.
///
/// Failing to resolve a token throws <see cref="OrbitAuthException"/>
/// rather than returning null — the receive pipeline cannot proceed
/// without bearer credentials, and surfacing the failure here gives
/// the agent a clear "I need a token" signal.
/// </summary>
public sealed class CompositeOrbitTokenSource : IOrbitTokenSource
{
    private readonly IReadOnlyList<IOrbitTokenSource> _chain;

    public CompositeOrbitTokenSource(params IOrbitTokenSource[] chain)
        : this((IReadOnlyList<IOrbitTokenSource>)chain) { }

    public CompositeOrbitTokenSource(IReadOnlyList<IOrbitTokenSource> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
            throw new ArgumentException("Composite chain must contain at least one source.", nameof(chain));
        _chain = chain;
    }

    /// <summary>Build the default env -> file chain.</summary>
    public static CompositeOrbitTokenSource Default() =>
        new(new EnvOrbitTokenSource(), new FileOrbitTokenSource());

    public async Task<string?> GetTokenAsync(ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var source in _chain)
        {
            ct.ThrowIfCancellationRequested();
            var token = await source.GetTokenAsync(server, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve a token or throw <see cref="OrbitAuthException"/>. Used
    /// by the CLI when a missing token is a hard failure; tests that
    /// want the soft-null behaviour stick with
    /// <see cref="GetTokenAsync"/>.
    /// </summary>
    public async Task<string> RequireTokenAsync(ServerConfig server, CancellationToken ct)
    {
        var token = await GetTokenAsync(server, ct).ConfigureAwait(false);
        if (token is null)
        {
            throw new OrbitAuthException(
                $"No ORBIT bearer token resolved for server '{server.Name}'. " +
                $"Set ORBIT_PAT_{server.Name.ToUpperInvariant()} or sign in via " +
                "the PRISM Agent so a token is written to " +
                $"%LOCALAPPDATA%\\PRISM.Visualiser\\auth\\{server.Name}.json.");
        }
        return token;
    }
}

/// <summary>Hard auth failure: no source supplied a token.</summary>
public sealed class OrbitAuthException : Exception
{
    public OrbitAuthException(string message) : base(message) { }
    public OrbitAuthException(string message, Exception inner) : base(message, inner) { }
}
