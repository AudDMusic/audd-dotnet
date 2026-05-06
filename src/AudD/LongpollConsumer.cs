using System.Text.Json;
using AudD.Internal;

namespace AudD;

/// <summary>
/// Tokenless longpoll consumer for browser/widget/extension use cases. Carries
/// no api_token. The category alone authorizes the subscription. Whoever derived
/// the category is responsible for ensuring a callback URL is set on their
/// account (we can't preflight that without a token). See spec §4.1, §6.7.
///
/// Hardening:
/// <list type="bullet">
///   <item>HTTP non-2xx → <see cref="AudDServerException"/> (not silent loop forever)</item>
///   <item>2xx with non-JSON body → <see cref="AudDSerializationException"/></item>
///   <item>READ-class retries on 5xx + transport errors</item>
///   <item>Configurable <c>maxRetries</c> / <c>backoffFactor</c></item>
/// </list>
/// </summary>
public sealed class LongpollConsumer : IDisposable, IAsyncDisposable
{
    /// <summary>The longpoll endpoint URL.</summary>
    public const string LongpollUrl = "https://api.audd.io/longpoll/";

    private const int HttpClientErrorFloor = 400;

    private readonly string _category;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly RetryPolicy _policy;
    private bool _disposed;

    /// <summary>Construct a tokenless consumer for a given longpoll category.</summary>
    public LongpollConsumer(
        string category,
        HttpClient? httpClient = null,
        int maxRetries = 3,
        double backoffFactor = 0.5)
    {
        if (string.IsNullOrEmpty(category)) throw new ArgumentException("category required", nameof(category));
        _category = category;
        if (httpClient is not null)
        {
            _client = httpClient;
            _ownsClient = false;
        }
        else
        {
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Build());
        }
        _policy = new RetryPolicy(RetryClass.Read, maxRetries < 1 ? 1 : maxRetries, backoffFactor);
    }

    /// <summary>
    /// Iterate over longpoll responses until cancellation. Yields a <see cref="JsonElement"/>
    /// per response (timeout or event payload).
    /// </summary>
    public async IAsyncEnumerable<JsonElement> IterateAsync(
        long? sinceTime = null,
        int timeout = 50,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var since = sinceTime;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var qs = new Dictionary<string, string>
            {
                ["category"] = _category,
                ["timeout"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (since.HasValue) qs["since_time"] = since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            HttpResponseEnvelope envelope;
            try
            {
                envelope = await Retry.RunAsync(
                    ct => SendAsync(qs, ct),
                    _policy,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exc)
            {
                throw new AudDConnectionException(exc.Message, exc);
            }

            if (envelope.HttpStatus >= HttpClientErrorFloor)
            {
                throw new AudDServerException(
                    errorCode: 0,
                    serverMessage: $"Longpoll endpoint returned HTTP {envelope.HttpStatus}",
                    httpStatus: envelope.HttpStatus,
                    requestId: envelope.RequestId);
            }
            if (envelope.JsonBody is null || envelope.JsonBody.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AudDSerializationException("Longpoll response was not a JSON object", envelope.RawText);
            }

            var body = envelope.JsonBody.Value;
            yield return body;
            if (body.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
            {
                since = ts.GetInt64();
            }
        }
    }

    private async Task<HttpResponseEnvelope> SendAsync(IDictionary<string, string> qs, CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder(LongpollUrl);
        sb.Append('?');
        bool first = true;
        foreach (var kv in qs)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
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

        string? rid = null;
        if (resp.Headers.TryGetValues("X-Request-Id", out var rids)) rid = rids.FirstOrDefault();
        else if (resp.Headers.TryGetValues("x-request-id", out var rids2)) rid = rids2.FirstOrDefault();

        return new HttpResponseEnvelope
        {
            JsonBody = body,
            HttpStatus = (int)resp.StatusCode,
            RequestId = rid,
            RawText = raw,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
