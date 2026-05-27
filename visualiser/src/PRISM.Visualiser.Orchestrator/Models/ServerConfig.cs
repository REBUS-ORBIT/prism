namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// ORBIT environment endpoints the orchestrator can target. Phase C
/// fills in the real URLs Phase B left as empty placeholders. The set
/// matches the canonical values documented in
/// <c>D:\Documents\Claude\REBUS System\CLAUDE.md</c> and used by the
/// PRISM Agent / Rhino connector / web app.
///
/// <para>
/// <see cref="BaseUrl"/> is the only mandatory field: every REST
/// endpoint the receive pipeline hits is rooted there. <see cref="GraphqlUrl"/>
/// is kept for the future GraphQL surface a connector might use; both
/// it and <see cref="BlobUrl"/> derive from <see cref="BaseUrl"/> when
/// not supplied explicitly.
/// </para>
/// </summary>
public sealed record ServerConfig(
    string Name,
    string BaseUrl,
    string GraphqlUrl,
    string BlobUrl)
{
    /// <summary>
    /// Production ORBIT server (<c>https://orbit.rebus.industries</c>).
    /// Real value, not a placeholder — Phase C wires the receive
    /// pipeline against this URL.
    /// </summary>
    public static ServerConfig Prod { get; } = FromBase(
        name: "prod",
        baseUrl: "https://orbit.rebus.industries");

    /// <summary>
    /// Dev ORBIT server (<c>https://orbit-dev.rebus.industries</c>).
    /// </summary>
    public static ServerConfig Dev { get; } = FromBase(
        name: "dev",
        baseUrl: "https://orbit-dev.rebus.industries");

    /// <summary>
    /// Build a <see cref="ServerConfig"/> from a base URL, deriving
    /// the REST / blob / GraphQL endpoints. Trailing slashes are
    /// trimmed so concatenation against <c>"/api/v1/..."</c> always
    /// yields a single slash.
    /// </summary>
    public static ServerConfig FromBase(string name, string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.TrimEnd('/');
        return new ServerConfig(
            Name: name,
            BaseUrl: trimmed,
            GraphqlUrl: $"{trimmed}/graphql",
            BlobUrl: $"{trimmed}/api/stream");
    }

    /// <summary>Resolve a config for the given <c>--server</c> selector.</summary>
    public static ServerConfig Resolve(string selector) => selector switch
    {
        "prod" => Prod,
        "dev" => Dev,
        _ => throw new ArgumentException(
            $"Unknown server selector '{selector}'. Expected 'prod' or 'dev'.",
            nameof(selector)),
    };
}
