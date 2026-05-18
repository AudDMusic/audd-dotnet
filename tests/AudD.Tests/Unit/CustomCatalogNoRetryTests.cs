using AudD;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Custom-catalog upload is metered per call. The SDK MUST NOT auto-retry — even on
/// otherwise-retryable transient failures — because a retry could double-charge the
/// account for the same fingerprinting work. These tests pin that contract: regardless
/// of the user-configured maxRetries (here a deliberately-high value of 5), exactly
/// ONE HTTP attempt is issued per AddAsync call for both 5xx and pre-upload connect
/// errors. Other mutating operations (Streams.AddAsync, Streams.SetCallbackUrlAsync,
/// Streams.DeleteAsync) keep their RetryClass.Mutating policy because they are
/// server-idempotent on radioId; only custom-catalog upload is special-cased.
/// </summary>
public class CustomCatalogNoRetryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _client;

    public CustomCatalogNoRetryTests()
    {
        var handler = new RewriteToWireMockHandler(new Uri(_server.Url!));
        _client = new HttpClient(handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task AddAsync_5xx_DoesNotRetry_ExactlyOneAttempt()
    {
        // 5xx normally has no effect on RetryClass.Mutating either (status retries are
        // already off for that class), but pin the count anyway so a future tweak that
        // adds 5xx-retry to mutating ops won't silently re-enable double-billing here.
        _server.Given(Request.Create().WithPath("/upload/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(503)
                   .WithHeader("Content-Type", "text/html")
                   .WithBody("<html>Service Unavailable</html>"));

        // maxRetries=5 deliberately exceeds the historical default of 3 — proves the
        // override happens inside CustomCatalog and not via AudD-level config.
        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client,
            maxRetries: 5);

        await Assert.ThrowsAsync<AudDServerException>(
            () => audd.CustomCatalog.AddAsync(123, new byte[] { 1, 2, 3 }));

        var attempts = _server.LogEntries.Count(e => e.RequestMessage.Path == "/upload/");
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AddAsync_PreUploadConnectError_DoesNotRetry_ExactlyOneAttempt()
    {
        // Use a counting handler that throws HttpRequestException on every send. Under
        // RetryClass.Mutating with the standard maxRetries, this would normally retry
        // (pre-upload connect failures are the one transport error class that policy
        // does retry on). The 1-attempt override in CustomCatalog must defeat that.
        var countingHandler = new ThrowingCountingHandler();
        using var throwClient = new HttpClient(countingHandler);

        await using var audd = new global::AudD.AudD("test", httpClient: throwClient, enterpriseHttpClient: throwClient,
            maxRetries: 5);

        await Assert.ThrowsAsync<AudDConnectionException>(
            () => audd.CustomCatalog.AddAsync(456, new byte[] { 1, 2, 3 }));

        Assert.Equal(1, countingHandler.Calls);
    }

    private sealed class ThrowingCountingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new HttpRequestException("simulated pre-upload connect failure");
        }
    }

    /// <summary>
    /// Local copy of the URL-rewriter used by other test fixtures. Keeps this file
    /// self-contained so it can run without leaking into AudDClientHttpTests state.
    /// </summary>
    private sealed class RewriteToWireMockHandler : DelegatingHandler
    {
        private readonly Uri _target;
        public RewriteToWireMockHandler(Uri target) : base(new HttpClientHandler())
        {
            _target = target;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var u = request.RequestUri!;
            var b = new UriBuilder(u)
            {
                Scheme = _target.Scheme,
                Host = _target.Host,
                Port = _target.Port,
            };
            request.RequestUri = b.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
