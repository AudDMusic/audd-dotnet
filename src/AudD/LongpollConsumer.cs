using AudD.Internal;

namespace AudD;

/// <summary>
/// Tokenless longpoll consumer for browser/widget/extension use cases. Carries
/// no api_token. The category alone authorizes the subscription. Whoever derived
/// the category is responsible for ensuring a callback URL is set on their
/// account (we can't preflight that without a token).
///
/// <para>Iterate via <see cref="Iterate"/>, which returns a
/// <see cref="LongpollPoll"/> handle with three typed streams (Matches /
/// Notifications / Errors). Disposing the consumer or the poll stops the
/// background fetch loop, including a request or a wait in progress.</para>
///
/// <para>Hardening:</para>
/// <list type="bullet">
///   <item><description>An error response from the server (e.g. a <c>timeout</c> outside 1–120)
///     → <see cref="AudDInvalidRequestException"/> on the Errors stream, and polling stops.</description></item>
///   <item><description>HTTP non-2xx after the retries → <see cref="AudDServerException"/> on Errors.</description></item>
///   <item><description>Transport errors after the retries → <see cref="AudDConnectionException"/> on Errors.</description></item>
///   <item><description>READ-class retries on 408/429/5xx and transport errors, with
///     configurable <c>maxRetries</c> / <c>backoffFactor</c>.</description></item>
///   <item><description>A 2xx body in none of the documented shapes (including non-JSON)
///     delivers nothing; the next request waits 1 second, doubling up to 30 seconds
///     while such responses continue.</description></item>
/// </list>
/// </summary>
public sealed class LongpollConsumer : IDisposable, IAsyncDisposable
{
    /// <summary>The longpoll endpoint URL.</summary>
    public const string LongpollUrl = "https://api.audd.io/longpoll/";

    private const int LongpollTimeoutMarginSeconds = 15;

    private readonly string _category;
    private readonly HttpTransport _http;
    private readonly RetryPolicy _policy;
    private readonly CancellationTokenSource _disposed = new();
    private int _disposedFlag;

    /// <summary>
    /// Construct a tokenless consumer for a given longpoll category. An injected
    /// <paramref name="httpClient"/>'s own <see cref="HttpClient.Timeout"/> must be
    /// longer than the poll <c>timeout</c>; the SDK's own client has none and gives
    /// each request a deadline of the poll timeout plus 15 seconds.
    /// </summary>
    public LongpollConsumer(
        string category,
        HttpClient? httpClient = null,
        int maxRetries = 3,
        double backoffFactor = 0.5)
    {
        if (string.IsNullOrEmpty(category)) throw new ArgumentException("category required", nameof(category));
        _category = category;
        // Tokenless transport: only GetWithoutAuthAsync is used, so no api_token is ever sent.
        _http = new HttpTransport(apiToken: string.Empty, httpClient, defaultTimeout: null);
        _policy = new RetryPolicy(RetryClass.Read, maxRetries < 1 ? 1 : maxRetries, backoffFactor);
    }

    /// <summary>Replaces the wait between requests that moved nothing forward (tests only).</summary>
    internal Func<TimeSpan, CancellationToken, Task>? IdleDelay { get; set; }

    /// <summary>
    /// Start a longpoll subscription. Returns a <see cref="LongpollPoll"/> handle.
    /// Dispose the handle (or the consumer) to stop polling. Each event reaches
    /// Matches or Notifications once, in order; "no events before timeout"
    /// responses deliver nothing.
    /// </summary>
    /// <param name="sinceTime">Server timestamp (milliseconds) to resume after; only newer events are delivered.</param>
    /// <param name="timeout">Seconds the server holds each request open waiting for events, 1 to 120 (default 50).</param>
    /// <param name="cancellationToken">Cancels the poll.</param>
    public LongpollPoll Iterate(
        long? sinceTime = null,
        int timeout = 50,
        CancellationToken cancellationToken = default)
    {
        if (System.Threading.Volatile.Read(ref _disposedFlag) != 0)
        {
            throw new ObjectDisposedException(nameof(LongpollConsumer));
        }
        var capturedTimeout = timeout;
        var requestDeadline = TimeSpan.FromSeconds(Math.Max(0, capturedTimeout) + LongpollTimeoutMarginSeconds);
        var fetcher = new LongpollFetcher(
            fetchAsync: async (since, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var qs = new Dictionary<string, string>
                {
                    ["category"] = _category,
                    ["timeout"] = capturedTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (since.HasValue) qs["since_time"] = since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

                HttpResponseEnvelope envelope;
                try
                {
                    envelope = await Retry.RunAsync(
                        c => _http.GetWithoutAuthAsync(LongpollUrl, qs, c, requestTimeout: requestDeadline),
                        _policy,
                        ct).ConfigureAwait(false);
                }
                catch (HttpRequestException exc)
                {
                    throw new AudDConnectionException(exc.Message, exc);
                }
                catch (TaskCanceledException exc) when (!ct.IsCancellationRequested)
                {
                    // The request deadline fired, not the caller's token.
                    throw new AudDConnectionException(exc.Message, exc);
                }
                return LongpollResponse.Read(envelope);
            },
            initialSinceTime: sinceTime,
            delay: IdleDelay);

        return LongpollPoll.Start(fetcher, cancellationToken, _disposed.Token);
    }

    /// <summary>Stop every poll started by <see cref="Iterate"/> and release the HTTP client the consumer owns.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        try { _disposed.Cancel(); } catch (AggregateException) { /* a cancellation callback threw; still tear down */ }
        _disposed.Dispose();
        _http.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
