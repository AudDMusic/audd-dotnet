using System.Text.Json;

namespace AudD.Internal;

/// <summary>Parsed HTTP response wrapper used by the SDK transport layer.</summary>
internal sealed class HttpResponseEnvelope
{
    public JsonElement? JsonBody { get; init; }
    public int HttpStatus { get; init; }
    public string? RequestId { get; init; }
    public string RawText { get; init; } = "";
}

/// <summary>
/// Thin async-only HTTP wrapper around <see cref="HttpClient"/>. Handles
/// User-Agent, api_token injection, x-request-id capture, and retries.
/// </summary>
internal sealed class HttpTransport : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private string _apiToken;

    public HttpTransport(string apiToken, HttpClient? injected, TimeSpan? defaultTimeout)
    {
        _apiToken = apiToken;
        if (injected is not null)
        {
            _client = injected;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient();
            if (defaultTimeout.HasValue)
            {
                _client.Timeout = defaultTimeout.Value;
            }
            _ownsClient = true;
        }
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Build());
        }
    }

    /// <summary>
    /// Read the current api_token via a volatile read so concurrent rotators
    /// observe the latest write. Each request snapshots once and uses that.
    /// </summary>
    private string CurrentToken() => System.Threading.Volatile.Read(ref _apiToken);

    /// <summary>
    /// Atomically swap the api_token used by subsequent requests. In-flight
    /// requests continue with the token they snapshotted at send time.
    /// </summary>
    public void SetApiToken(string newToken)
    {
        System.Threading.Interlocked.Exchange(ref _apiToken, newToken);
    }

    public async Task<HttpResponseEnvelope> PostFormAsync(
        string url,
        Func<IDictionary<string, string>, HttpContent> contentFactory,
        IDictionary<string, string>? extraFields,
        CancellationToken cancellationToken)
    {
        var fields = extraFields is null ? new Dictionary<string, string>() : new Dictionary<string, string>(extraFields);
        fields["api_token"] = CurrentToken();

        using var content = contentFactory(fields);
        return await SendAsync(HttpMethod.Post, url, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseEnvelope> GetAsync(
        string url,
        IDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var qs = new Dictionary<string, string>(query ?? new Dictionary<string, string>());
        if (!qs.ContainsKey("api_token"))
        {
            qs["api_token"] = CurrentToken();
        }
        var fullUrl = AppendQueryString(url, qs);
        return await SendAsync(HttpMethod.Get, fullUrl, content: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseEnvelope> GetWithoutAuthAsync(
        string url,
        IDictionary<string, string>? query,
        CancellationToken cancellationToken)
    {
        var qs = new Dictionary<string, string>(query ?? new Dictionary<string, string>());
        var fullUrl = AppendQueryString(url, qs);
        return await SendAsync(HttpMethod.Get, fullUrl, content: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseEnvelope> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(
#if NET6_0
            cancellationToken
#endif
        ).ConfigureAwait(false);

        JsonElement? body = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            body = null;
        }

        string? requestId = null;
        if (resp.Headers.TryGetValues("X-Request-Id", out var rids))
        {
            requestId = rids.FirstOrDefault();
        }
        else if (resp.Headers.TryGetValues("x-request-id", out var rids2))
        {
            requestId = rids2.FirstOrDefault();
        }

        return new HttpResponseEnvelope
        {
            JsonBody = body,
            HttpStatus = (int)resp.StatusCode,
            RequestId = requestId,
            RawText = raw,
        };
    }

    private static string AppendQueryString(string url, IDictionary<string, string> qs)
    {
        if (qs.Count == 0) return url;
        var sb = new System.Text.StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        bool first = true;
        foreach (var kv in qs)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
