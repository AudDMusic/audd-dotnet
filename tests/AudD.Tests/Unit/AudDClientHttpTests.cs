using System.Net;
using AudD;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace AudD.Tests.Unit;

public class AudDClientHttpTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _client;

    public AudDClientHttpTests()
    {
        // Route api.audd.io & enterprise.audd.io to wiremock by overriding BaseAddress via DNS isn't easy;
        // instead we route every URL the SDK builds through the wiremock host by using a DelegatingHandler
        // that rewrites the URL. WireMock.Net doesn't transparent-proxy hostnames by itself.
        var handler = new RewriteHostHandler(new Uri(_server.Url!));
        _client = new HttpClient(handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task RecognizeAsync_PublicMatch_ReturnsRecognitionResult()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":{"timecode":"00:56","artist":"Tears For Fears","title":"Everybody Wants To Rule The World","song_link":"https://lis.tn/NbkVb"}}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(result);
        Assert.Equal("Tears For Fears", result!.Artist);
        Assert.Equal("https://lis.tn/NbkVb?thumb", result.ThumbnailUrl);
    }

    [Fact]
    public async Task RecognizeAsync_NoMatch_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":null}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.Null(result);
    }

    [Fact]
    public async Task RecognizeAsync_AuthError_RaisesAuthenticationException()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"error","error":{"error_code":900,"error_message":"bad"}}"""));

        await using var audd = new global::AudD.AudD("bad", httpClient: _client, enterpriseHttpClient: _client);
        var ex = await Assert.ThrowsAsync<AudDAuthenticationException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));
        Assert.Equal(900, ex.ErrorCode);
    }

    [Fact]
    public async Task RecognizeAsync_NonJson5xx_RaisesAudDServerException()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(502)
                   .WithHeader("Content-Type", "text/html")
                   .WithBody("<html>Bad Gateway</html>"));

        await using var audd = new global::AudD.AudD("t", httpClient: _client, enterpriseHttpClient: _client,
            maxRetries: 1);
        var ex = await Assert.ThrowsAsync<AudDServerException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));
        Assert.Equal(502, ex.HttpStatus);
    }

    [Fact]
    public async Task RecognizeAsync_2xxBadJson_RaisesSerializationException()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("not-json"));

        await using var audd = new global::AudD.AudD("t", httpClient: _client, enterpriseHttpClient: _client,
            maxRetries: 1);
        await Assert.ThrowsAsync<AudDSerializationException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));
    }

    [Fact]
    public async Task Streams_GetCallbackUrl_ReturnsString()
    {
        _server.Given(Request.Create().WithPath("/getCallbackUrl/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":"https://example.com/cb"}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var url = await audd.Streams.GetCallbackUrlAsync();
        Assert.Equal("https://example.com/cb", url);
    }

    [Fact]
    public async Task Streams_LongpollPreflight_NoCallbackSet_RaisesInvalidRequest()
    {
        _server.Given(Request.Create().WithPath("/getCallbackUrl/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"error","error":{"error_code":19,"error_message":"Internal error."}}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var ex = await Assert.ThrowsAsync<AudDInvalidRequestException>(async () =>
        {
            await foreach (var _ in audd.Streams.LongpollAsync("cat-9chars"))
            {
                break;
            }
        });
        Assert.Contains("no callback URL", ex.ServerMessage);
    }

    [Fact]
    public async Task Streams_LongpollPreflight_SkipBypass_DoesNotPreflight()
    {
        _server.Given(Request.Create().WithPath("/getCallbackUrl/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"error","error":{"error_code":19,"error_message":"Internal error."}}"""));

        _server.Given(Request.Create().WithPath("/longpoll/").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"timeout":"no events before timeout","timestamp":1777901270049}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);
        var iterations = 0;
        try
        {
            await foreach (var ev in audd.Streams.LongpollAsync("c", skipCallbackCheck: true, cancellationToken: cts.Token))
            {
                iterations++;
                if (iterations >= 1) break;
            }
        }
        catch (OperationCanceledException) { }
        Assert.True(iterations >= 1);
    }

    [Fact]
    public async Task CustomCatalog_AddAsync_904_RaisesCustomCatalogAccessException()
    {
        _server.Given(Request.Create().WithPath("/upload/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"error","error":{"error_code":904,"error_message":"only paid"}}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var ex = await Assert.ThrowsAsync<AudDCustomCatalogAccessException>(
            () => audd.CustomCatalog.AddAsync(123, new byte[] { 1, 2, 3 }));
        Assert.Equal("only paid", ex.OriginalServerMessage);
        Assert.Contains("Adding songs to your custom catalog", ex.Message);
    }

    [Fact]
    public async Task RecognizeEnterprise_ReturnsListOfMatches()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""
                   {"status":"success","result":[{"songs":[{"score":81,"timecode":"00:57","artist":"x","title":"y","isrc":"GBUM71403885","upc":"00602547037169"}],"offset":"00:00"}]}
                   """));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var matches = await audd.RecognizeEnterpriseAsync("https://x.example/y.mp3", limit: 1);
        Assert.Single(matches);
        Assert.Equal("GBUM71403885", matches[0].Isrc);
    }

    [Fact]
    public async Task Advanced_RawRequest_ReturnsBody()
    {
        _server.Given(Request.Create().WithPath("/findLyrics/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":[{"artist":"x","title":"y"}]}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var lyrics = await audd.Advanced.FindLyricsAsync("hello");
        Assert.Single(lyrics);
        Assert.Equal("x", lyrics[0].Artist);
    }

    /// <summary>
    /// Rewrites every outbound URL host/port to the WireMock server, leaving the
    /// path/query intact. Lets us point the SDK at api.audd.io / enterprise.audd.io
    /// without DNS hijacking.
    /// </summary>
    private sealed class RewriteHostHandler : DelegatingHandler
    {
        private readonly Uri _target;
        public RewriteHostHandler(Uri target) : base(new HttpClientHandler())
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
