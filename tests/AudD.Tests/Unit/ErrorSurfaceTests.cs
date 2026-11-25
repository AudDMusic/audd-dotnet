using System.Net;
using System.Text;
using AudD;
using AudD.DependencyInjection;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Failures reach callers as <see cref="AudDException"/> subtypes, and nothing a
/// caller might log (ToString of the client, its options, its exceptions and
/// events) carries the api_token.
/// </summary>
public class ErrorSurfaceTests
{
    private const string Token = "surface-secret-token-91c2";

    // ---- Timeouts map to AudDConnectionException on every endpoint ----

    [Fact]
    public async Task Streams_ClientTimeout_IsAudDConnectionException()
    {
        using var handler = new FuncHandler((_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client);

        await Assert.ThrowsAsync<AudDConnectionException>(() => audd.Streams.ListAsync());
        await Assert.ThrowsAsync<AudDConnectionException>(() => audd.Streams.SetCallbackUrlAsync("https://example.com/cb"));
    }

    [Fact]
    public async Task CustomCatalog_ClientTimeout_IsAudDConnectionException()
    {
        using var handler = new FuncHandler((_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client);

        await Assert.ThrowsAsync<AudDConnectionException>(() => audd.CustomCatalog.AddAsync(1, new byte[] { 1, 2, 3 }));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Advanced_ClientTimeout_IsAudDConnectionException()
    {
        using var handler = new FuncHandler((_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"));
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client);

        await Assert.ThrowsAsync<AudDConnectionException>(() => audd.Advanced.RawRequestAsync("getStreams"));
    }

    [Fact]
    public async Task CallerCancellation_StaysOperationCanceled()
    {
        using var handler = new FuncHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audd.Streams.ListAsync(cts.Token));
    }

    // ---- An unseekable stream that can't be re-sent reports the real failure ----

    [Fact]
    public async Task UnseekableStream_After5xx_SurfacesTheServerError()
    {
        using var handler = new FuncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>Service Unavailable</html>", Encoding.UTF8, "text/html"),
        }));
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client, maxRetries: 3);
        using var stream = new NonSeekableStream(new MemoryStream(new byte[] { 1, 2, 3 }));

        var ex = await Assert.ThrowsAsync<AudDServerException>(() => audd.RecognizeAsync(stream));
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnseekableStream_AfterConnectionFailure_SurfacesAudDConnectionException()
    {
        using var handler = new FuncHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);
        await using var audd = NewClient(client, maxRetries: 3);
        using var stream = new NonSeekableStream(new MemoryStream(new byte[] { 1, 2, 3 }));

        var ex = await Assert.ThrowsAsync<AudDConnectionException>(() => audd.RecognizeAsync(stream));
        Assert.Contains("connection refused", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    // ---- The token stays out of ToString, messages and URLs ----

    [Fact]
    public async Task Token_NeverAppearsInToString_ExceptionMessages_OrEventUrls()
    {
        var events = new List<AudDEvent>();
        // A handler that echoes the full request URL into its error, as some proxies do.
        using var handler = new FuncHandler((req, _) => throw new HttpRequestException($"failed: {req.Method} {req.RequestUri}"));
        using var client = new HttpClient(handler);
        await using var audd = new global::AudD.AudD(Token, maxRetries: 1, backoffFactor: 0,
            httpClient: client, enterpriseHttpClient: client, onEvent: e => { lock (events) events.Add(e); });

        var failures = new List<Exception>
        {
            await Assert.ThrowsAsync<AudDConnectionException>(() => audd.RecognizeAsync("https://example.com/a.mp3")),
            await Assert.ThrowsAsync<AudDConnectionException>(() => audd.RecognizeEnterpriseAsync("https://example.com/a.mp3", limit: 1)),
            await Assert.ThrowsAsync<AudDConnectionException>(() => audd.Streams.ListAsync()),
            await Assert.ThrowsAsync<AudDConnectionException>(() => audd.CustomCatalog.AddAsync(1, new byte[] { 1 })),
            await Assert.ThrowsAsync<AudDConnectionException>(() => audd.Advanced.RawRequestAsync("getStreams")),
        };

        Assert.DoesNotContain(Token, audd.ToString());
        Assert.DoesNotContain(Token, audd.Streams.ToString());
        Assert.DoesNotContain(Token, new AudDOptions { ApiToken = Token }.ToString());
        foreach (var f in failures)
        {
            Assert.DoesNotContain(Token, f.ToString());
        }
        Assert.NotEmpty(events);
        foreach (var e in events)
        {
            Assert.DoesNotContain(Token, e.ToString());
        }
    }

    // ---- Helpers ----

    private static global::AudD.AudD NewClient(HttpClient client, int maxRetries = 1)
        => new(Token, maxRetries: maxRetries, backoffFactor: 0, httpClient: client, enterpriseHttpClient: client);

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private int _calls;

        public FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) { _respond = respond; }

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _respond(request, cancellationToken);
        }
    }

    private sealed class NonSeekableStream : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        public NonSeekableStream(System.IO.Stream inner) { _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
