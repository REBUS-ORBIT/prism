using System.Text.Json;
using System.Text.Json.Serialization;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Auth;

/// <summary>
/// Reads the ORBIT bearer token from the on-disk store the PRISM Agent
/// and the Rhino connector both write into. The schema mirrors the
/// connector convention (small JSON file, one per environment) so a
/// user who already authed in Rhino does not have to re-auth here.
///
/// Layout:
/// <code>
/// %LOCALAPPDATA%\PRISM.Visualiser\auth\prod.json
/// %LOCALAPPDATA%\PRISM.Visualiser\auth\dev.json
/// </code>
///
/// Schema:
/// <code>
/// {
///   "schema": "prism-visualiser/auth/v1",
///   "server": "prod",
///   "token": "ghp_...",
///   "savedAt": "2026-05-27T16:00:00Z",
///   "savedBy": "prism-agent" | "prism-visualiser" | "rhino-connector"
/// }
/// </code>
///
/// Phase C only reads. Phase D+ will add a write path so the agent can
/// hand the orchestrator a fresh PAT post-OAuth without any process
/// arg-passing.
/// </summary>
public sealed class FileOrbitTokenSource : IOrbitTokenSource
{
    /// <summary>Schema string written into <c>schema</c> for forward compat.</summary>
    public const string SchemaName = "prism-visualiser/auth/v1";

    private readonly string _authRoot;

    public FileOrbitTokenSource()
        : this(ResolveDefaultAuthRoot()) { }

    public FileOrbitTokenSource(string authRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authRoot);
        _authRoot = authRoot;
    }

    /// <summary><c>%LOCALAPPDATA%\PRISM.Visualiser\auth</c>.</summary>
    public static string ResolveDefaultAuthRoot()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(local, "PRISM.Visualiser", "auth");
    }

    /// <summary>The on-disk path this source would consult for <paramref name="server"/>.</summary>
    public string ResolvePath(ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Path.Combine(_authRoot, $"{server.Name}.json");
    }

    public async Task<string?> GetTokenAsync(ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var path = ResolvePath(server);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        try
        {
            var record = await JsonSerializer
                .DeserializeAsync(stream, AuthRecordContext.Default.AuthRecord, ct)
                .ConfigureAwait(false);
            if (record is null) return null;
            return string.IsNullOrWhiteSpace(record.Token) ? null : record.Token.Trim();
        }
        catch (JsonException)
        {
            // Corrupt / partial file. Treat it as "no token" so the
            // composite chain can fail with a friendlier message
            // ("no source supplied a token") rather than spilling a
            // System.Text.Json stack trace at the agent.
            return null;
        }
    }

    /// <summary>Persist a token for <paramref name="server"/> atomically.</summary>
    public async Task SaveAsync(ServerConfig server, string token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(_authRoot);

        var record = new AuthRecord(
            Schema: SchemaName,
            Server: server.Name,
            Token: token,
            SavedAt: DateTimeOffset.UtcNow,
            SavedBy: "prism-visualiser");

        var target = ResolvePath(server);
        var temp = target + ".tmp";
        await using (var fs = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(fs, record, AuthRecordContext.Default.AuthRecord, ct)
                .ConfigureAwait(false);
        }
        File.Move(temp, target, overwrite: true);
    }
}

/// <summary>
/// On-disk auth record schema. Internal because callers should go
/// through <see cref="FileOrbitTokenSource"/>; the type is public only
/// because <see cref="JsonSerializerContext"/> requires it.
/// </summary>
public sealed record AuthRecord(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt,
    [property: JsonPropertyName("savedBy")] string SavedBy);

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for
/// <see cref="AuthRecord"/>. Keeps the auth path AOT/trim-safe and
/// removes the IL2026 warning <c>JsonSerializer.DeserializeAsync</c>
/// would otherwise emit under <c>EnableTrimAnalyzer</c>.
/// </summary>
[JsonSerializable(typeof(AuthRecord))]
internal sealed partial class AuthRecordContext : JsonSerializerContext { }
