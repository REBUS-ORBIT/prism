using System.Net;
using System.Net.Http.Headers;
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
///   * GET requests only (Phase C is a pure receive flow).
///   * Bearer token resolved <em>once</em> per <c>HttpOrbitApi</c>
///     instance via <see cref="IOrbitTokenSource"/>; rotation is
///     a Phase F concern.
///   * Polly retries on 408 / 429 / 5xx with exponential back-off
///     (200ms, 400ms, 800ms) — anything else propagates to the
///     caller as a typed <see cref="OrbitApiException"/>.
///
/// REST shape (per the receive-pipeline spec):
/// <code>
///   GET /api/v1/projects/{projectId}/versions/{versionId}
///   GET /api/v1/objects/{objectId}
///   GET /api/v1/projects/{projectId}/blobs/{blobHash}
/// </code>
///
/// All endpoint shapes are kept in one place (<see cref="EndpointTemplates"/>)
/// so Phase D can swap them for the canonical Speckle-server shape
/// (<c>/objects/{streamId}/{id}/single</c>) with a single edit.
/// </summary>
public sealed class HttpOrbitApi : IOrbitApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public HttpOrbitApi(HttpClient http, ServerConfig server, string bearerToken,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        _http = http;
        _ownsHttpClient = ownsHttpClient;
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

    public async Task<VersionDescriptor> GetVersionAsync(
        string projectId, string versionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var url = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            EndpointTemplates.Version, Uri.EscapeDataString(projectId),
            Uri.EscapeDataString(versionId));

        using var response = await SendWithRetriesAsync(url, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            var dto = JsonSerializer.Deserialize(
                json, OrbitApiJsonContext.Default.VersionResponseDto)
                ?? throw new OrbitApiException(
                    $"Empty version response for {projectId}/{versionId}.");
            return new VersionDescriptor(
                ProjectId: projectId,
                ModelId: dto.ModelId ?? string.Empty,
                VersionId: versionId,
                RootObjectId: dto.RootObjectId
                    ?? throw new OrbitApiException(
                        $"Version {projectId}/{versionId} has no rootObjectId."),
                Message: dto.Message,
                AuthorId: dto.AuthorId,
                CreatedAt: dto.CreatedAt);
        }
        catch (JsonException ex)
        {
            throw new OrbitApiException(
                $"Invalid version response JSON for {projectId}/{versionId}.", ex);
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

    /// <summary>REST endpoint templates. Indices match <see cref="string.Format(string,object?[])"/>.</summary>
    internal static class EndpointTemplates
    {
        public const string Version = "api/v1/projects/{0}/versions/{1}";
        public const string Object = "api/v1/projects/{0}/objects/{1}";
        public const string Blob = "api/v1/projects/{0}/blobs/{1}";
    }

    internal sealed record VersionResponseDto(
        [property: JsonPropertyName("rootObjectId")] string? RootObjectId,
        [property: JsonPropertyName("modelId")] string? ModelId,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("authorId")] string? AuthorId,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt);
}

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for ORBIT API
/// DTOs. Keeps <see cref="HttpOrbitApi"/> AOT/trim-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HttpOrbitApi.VersionResponseDto))]
internal sealed partial class OrbitApiJsonContext : JsonSerializerContext { }

/// <summary>Typed wrapper for any orchestrator-side ORBIT REST failure.</summary>
public sealed class OrbitApiException : Exception
{
    public OrbitApiException(string message) : base(message) { }
    public OrbitApiException(string message, Exception inner) : base(message, inner) { }
}
