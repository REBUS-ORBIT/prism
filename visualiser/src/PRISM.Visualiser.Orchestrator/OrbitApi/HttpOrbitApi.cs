using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Polly;
using Polly.Retry;

using PRISM.Visualiser.Orchestrator.Auth;
using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.OrbitApi;

/// <summary>
/// HTTP-backed <see cref="IOrbitApi"/>. Stays narrow on purpose:
///
///   * GET requests only for objects / blobs; version metadata
///     is resolved via a single GraphQL query (no REST endpoint
///     for versions exists in the ORBIT server).
///   * Bearer token resolved <em>once</em> per <c>HttpOrbitApi</c>
///     instance via <see cref="IOrbitTokenSource"/>; rotation is
///     a Phase F concern.
///   * Polly retries on 408 / 429 / 5xx with exponential back-off
///     (200ms, 400ms, 800ms) — anything else propagates to the
///     caller as a typed <see cref="OrbitApiException"/>.
///
/// Actual ORBIT REST shapes (verified against orbit.rebus.industries):
/// <code>
///   POST /graphql                              — version metadata
///   GET  /objects/{projectId}/{objectId}/single — object JSON body
///   GET  /blobs/{projectId}/{blobHash}          — binary blob
/// </code>
///
/// All endpoint shapes are kept in one place (<see cref="EndpointTemplates"/>)
/// so they can be updated with a single edit if the server moves them.
/// </summary>
public sealed class HttpOrbitApi : IOrbitApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _graphqlUrl;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public HttpOrbitApi(HttpClient http, ServerConfig server, string bearerToken,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _graphqlUrl = server.GraphqlUrl;
        _http.BaseAddress ??= new Uri(server.BaseUrl + "/", UriKind.Absolute);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(IsRetriable)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 200));
    }

    /// <summary>Construct against an ORBIT environment with a freshly created HttpClient.</summary>
    public static HttpOrbitApi Create(ServerConfig server, string bearerToken)
    {
        var http = new HttpClient();
        return new HttpOrbitApi(http, server, bearerToken, ownsHttpClient: true);
    }

    /// <summary>GraphQL query used to resolve a version → root object id.</summary>
    private const string VersionQuery =
        """
        query Version($projectId: String!, $versionId: String!) {
          project(id: $projectId) {
            version(id: $versionId) {
              id
              referencedObject
              message
              authorUser { id }
              createdAt
              model { id }
            }
          }
        }
        """;

    public async Task<VersionDescriptor> GetVersionAsync(
        string projectId, string versionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var requestBody = new GqlRequest(
            Query: VersionQuery,
            Variables: new GqlVersionVariables(projectId, versionId));

        var requestJson = JsonSerializer.Serialize(
            requestBody, OrbitApiJsonContext.Default.GqlRequest);

        using var response = await _retryPolicy.ExecuteAsync(async token =>
        {
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            content.Headers.Add("apollo-require-preflight", "true");
            return await _http.PostAsync(_graphqlUrl, content, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new OrbitApiException(
                $"GraphQL version query failed HTTP {(int)response.StatusCode} for " +
                $"{projectId}/{versionId}. Body: {Truncate(errBody, 512)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            var gql = JsonSerializer.Deserialize(
                json, OrbitApiJsonContext.Default.GqlVersionResponse)
                ?? throw new OrbitApiException(
                    $"Empty GraphQL response for {projectId}/{versionId}.");

            var node = gql.Data?.Project?.Version
                ?? throw new OrbitApiException(
                    $"GraphQL response has no version node for {projectId}/{versionId}. " +
                    $"Raw: {Truncate(json, 256)}");

            return new VersionDescriptor(
                ProjectId: projectId,
                ModelId: node.Model?.Id ?? string.Empty,
                VersionId: versionId,
                RootObjectId: node.ReferencedObject
                    ?? throw new OrbitApiException(
                        $"Version {projectId}/{versionId} has no referencedObject."),
                Message: node.Message,
                AuthorId: node.AuthorUser?.Id,
                CreatedAt: node.CreatedAt);
        }
        catch (JsonException ex)
        {
            throw new OrbitApiException(
                $"Invalid GraphQL response JSON for {projectId}/{versionId}.", ex);
        }
    }

    public async Task<Stream> GetObjectAsync(
        string projectId, string objectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var url = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            EndpointTemplates.Object, Uri.EscapeDataString(projectId),
            Uri.EscapeDataString(objectId));

        var response = await SendWithRetriesAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task<Stream> GetBlobAsync(
        string projectId, string blobHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobHash);

        var url = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            EndpointTemplates.Blob, Uri.EscapeDataString(projectId),
            Uri.EscapeDataString(blobHash));

        var response = await SendWithRetriesAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(string url, CancellationToken ct)
    {
        // Polly's HandleResult requires us to NOT dispose the inner
        // HttpResponseMessage on success — the policy returns the
        // last instance to the caller. Disposal is the caller's job.
        var response = await _retryPolicy
            .ExecuteAsync(async token =>
                await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new OrbitApiException(
                $"GET {url} failed with HTTP {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body (truncated): {Truncate(body, 512)}");
        }
        return response;
    }

    private static bool IsRetriable(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        return response.StatusCode == HttpStatusCode.RequestTimeout
            || response.StatusCode == HttpStatusCode.TooManyRequests
            || code >= 500 && code < 600;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    /// <summary>
    /// REST endpoint templates for object and blob fetches.
    /// Version metadata is resolved via GraphQL — see <see cref="VersionQuery"/>.
    /// </summary>
    internal static class EndpointTemplates
    {
        /// <summary>GET /objects/{projectId}/{objectId}/single</summary>
        public const string Object = "objects/{0}/{1}/single";

        /// <summary>GET /api/stream/{projectId}/blob/{blobId}</summary>
        public const string Blob = "api/stream/{0}/blob/{1}";
    }

    // ----------------------------------------------------------------
    // GraphQL DTOs
    // ----------------------------------------------------------------

    internal sealed record GqlRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] GqlVersionVariables Variables);

    internal sealed record GqlVersionVariables(
        [property: JsonPropertyName("projectId")] string ProjectId,
        [property: JsonPropertyName("versionId")] string VersionId);

    internal sealed record GqlVersionResponse(
        [property: JsonPropertyName("data")] GqlVersionData? Data);

    internal sealed record GqlVersionData(
        [property: JsonPropertyName("project")] GqlVersionProject? Project);

    internal sealed record GqlVersionProject(
        [property: JsonPropertyName("version")] GqlVersionNode? Version);

    internal sealed record GqlVersionNode(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("referencedObject")] string? ReferencedObject,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("authorUser")] GqlAuthorUser? AuthorUser,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("model")] GqlVersionModel? Model);

    internal sealed record GqlAuthorUser(
        [property: JsonPropertyName("id")] string? Id);

    internal sealed record GqlVersionModel(
        [property: JsonPropertyName("id")] string? Id);
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for ORBIT API
/// DTOs. Keeps <see cref="HttpOrbitApi"/> AOT/trim-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HttpOrbitApi.GqlRequest))]
[JsonSerializable(typeof(HttpOrbitApi.GqlVersionResponse))]
internal sealed partial class OrbitApiJsonContext : JsonSerializerContext { }

/// <summary>Typed wrapper for any orchestrator-side ORBIT REST failure.</summary>
public sealed class OrbitApiException : Exception
{
    public OrbitApiException(string message) : base(message) { }
    public OrbitApiException(string message, Exception inner) : base(message, inner) { }
}
