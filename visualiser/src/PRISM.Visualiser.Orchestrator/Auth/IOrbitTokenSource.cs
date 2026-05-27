using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Auth;

/// <summary>
/// Resolves an ORBIT bearer token for a given environment. Implementors
/// chain via <see cref="CompositeOrbitTokenSource"/>; the receive
/// pipeline only ever consumes the resolved token, never the source.
///
/// Token rotation is out-of-scope for Phase C: a token is fetched once
/// per <c>stream</c> invocation. Phase F will revisit refresh-on-401.
/// </summary>
public interface IOrbitTokenSource
{
    /// <summary>
    /// Resolve a bearer token for <paramref name="server"/>.
    /// Returns <c>null</c> when this source has no opinion on the
    /// requested environment (so the next link in the composite chain
    /// can take a turn). Throws only on hard IO / format errors.
    /// </summary>
    Task<string?> GetTokenAsync(ServerConfig server, CancellationToken ct);
}
