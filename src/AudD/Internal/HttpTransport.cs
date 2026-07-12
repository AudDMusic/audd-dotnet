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
    private readonly HttpClient? _client;
    private readonly Func<HttpClient>? _clientResolver;
    private readonly bool _ownsClient;
    private readonly TimeSpan? _defaultTimeout;
    private string _apiToken;

    /// <summary>
    /// The SDK's default per-request timeout for this transport (60s standard,
    /// 1h enterprise). Enforced via a linked cancellation token per request rather
    /// than <see cref="HttpClient.Timeout"/>, so a caller-supplied <c>timeout:</c>
    /// can both shorten and extend the effective deadline.
    /// </summary>
    public TimeSpan? DefaultTimeout => _defaultTimeout;

    public HttpTransport(string apiToken, HttpClient? injected, TimeSpan? defaultTimeout)
    {
        _apiToken = apiToken;
        _defaultTimeout = defaultTimeout;
        if (injected is not null)
        {
            _client = injected;
            _ownsClient = false;
        }
        else
        {
            // Disable HttpClient's own timeout: the SDK enforces the default
            // deadline per-request via a linked cancellation token instead. That
            // way a caller passing a larger per-call timeout is not silently
            // capped by the client's Timeout.
            _client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
        EnsureUserAgent(_client);
    }

    /// <summary>
    /// Construct a transport that resolves a fresh <see cref="HttpClient"/> per
    /// request from <paramref name="clientResolver"/> — the DI path backed by
    /// <c>IHttpClientFactory</c>, so handler rotation is preserved. The SDK's
    /// default deadline is still enforced per-request via a linked cancellation
    /// token, independent of the resolved client's own timeout.
    /// </summary>
    public HttpTransport(string apiToken, Func<HttpClient> clientResolver, TimeSpan? defaultTimeout)
    {
        _apiToken = apiToken;
        _defaultTimeout = defaultTimeout;
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
        _ownsClient = false;
    }

    private HttpClient ResolveClient()
    {
        if (_clientResolver is not null)
        {
            var c = _clientResolver();
            EnsureUserAgent(c);
            return c;
        }
        return _client!;
    }

    private static void EnsureUserAgent(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Build());
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
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var fields = extraFields is null ? new Dictionary<string, string>() : new Dictionary<string, string>(extraFields);
        fields["api_token"] = CurrentToken();

        using var content = contentFactory(fields);
        return await SendAsync(HttpMethod.Post, url, content, cancellationToken, requestTimeout).ConfigureAwait(false);
    }

    public async Task<HttpResponseEnvelope> GetAsync(
        string url,
        IDictionary<string, string>? query,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var qs = new Dictionary<string, string>(query ?? new Dictionary<string, string>());
        if (!qs.ContainsKey("api_token"))
        {
            qs["api_token"] = CurrentToken();
        }
        var fullUrl = AppendQueryString(url, qs);
        return await SendAsync(HttpMethod.Get, fullUrl, content: null, cancellationToken, requestTimeout).ConfigureAwait(false);
    }

    public async Task<HttpResponseEnvelope> GetWithoutAuthAsync(
        string url,
        IDictionary<string, string>? query,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var qs = new Dictionary<string, string>(query ?? new Dictionary<string, string>());
        var fullUrl = AppendQueryString(url, qs);
        return await SendAsync(HttpMethod.Get, fullUrl, content: null, cancellationToken, requestTimeout).ConfigureAwait(false);
    }

    private async Task<HttpResponseEnvelope> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout)
    {
        // Enforce the effective per-request deadline (the caller's explicit
        // timeout when supplied, otherwise this transport's default) with a
        // linked cancellation token. HttpClient.Timeout stays infinite on
        // owned clients, so a longer per-call timeout is honored rather than
        // capped. On an injected client the caller owns HttpClient.Timeout;
        // we still layer the SDK default deadline on top so behavior matches.
        var effectiveTimeout = requestTimeout ?? _defaultTimeout;
        CancellationTokenSource? timeoutCts = null;
        var effectiveToken = cancellationToken;
        if (effectiveTimeout.HasValue && effectiveTimeout.Value != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout.Value);
            effectiveToken = timeoutCts.Token;
        }
        try
        {
            return await SendCoreAsync(method, url, content, cancellationToken, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The SDK-enforced deadline fired (not the caller's token). Surface
            // as a TaskCanceledException so upstream maps it to a connection/timeout.
            throw new TaskCanceledException($"The request timed out after {effectiveTimeout!.Value}.");
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private async Task<HttpResponseEnvelope> SendCoreAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken callerToken,
        CancellationToken effectiveToken)
    {
        var client = ResolveClient();
        using var req = new HttpRequestMessage(method, url) { Content = content };
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, effectiveToken).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(
#if NET6_0_OR_GREATER
            effectiveToken
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
            _client!.Dispose();
        }
    }
}
