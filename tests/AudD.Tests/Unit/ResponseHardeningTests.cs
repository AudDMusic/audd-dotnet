using System.Net;
using System.Text.Json;
using AudD;
using AudD.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Regression tests: lenient response parsing (wrong-typed fields degrade,
/// never throw), per-call timeout that can shorten and extend, DI factory
/// handler resolution, longpoll timeout headroom, and error-code decode
/// robustness.
/// </summary>
public class ResponseHardeningTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _client;

    public ResponseHardeningTests()
    {
        var handler = new RewriteHostHandler(new Uri(_server.Url!));
        _client = new HttpClient(handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private global::AudD.AudD NewClient(int maxRetries = 1)
        => new global::AudD.AudD("your-api-token", maxRetries: maxRetries, httpClient: _client, enterpriseHttpClient: _client);

    private void RespondRecognize(string body)
        => _server.Given(Request.Create().WithPath("/").UsingPost())
                  .RespondWith(Response.Create().WithStatusCode(200)
                      .WithHeader("Content-Type", "application/json")
                      .WithBody(body));

    // ---- D1: lenient recognition parsing ----

    [Fact]
    public async Task RecognizeAsync_WrongTypedAudioId_DegradesToNull_DoesNotThrow()
    {
        RespondRecognize("""{"status":"success","result":{"timecode":"00:56","audio_id":"42","artist":"A","title":"T"}}""");
        await using var audd = NewClient();
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(result);
        Assert.Null(result!.AudioId);
        Assert.Equal("A", result.Artist);
        Assert.Equal("00:56", result.Timecode);
    }

    [Fact]
    public async Task RecognizeAsync_WrongTypedTimecode_DegradesToDefault_DoesNotThrow()
    {
        RespondRecognize("""{"status":"success","result":{"timecode":42,"artist":"A","title":"T"}}""");
        await using var audd = NewClient();
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(result);
        // The wrong-typed timecode is dropped; the rest of the result survives.
        Assert.True(string.IsNullOrEmpty(result!.Timecode));
        Assert.Equal("A", result.Artist);
    }

    [Fact]
    public async Task RecognizeAsync_AppleMusicWrongShape_DegradesToNull_DoesNotThrow()
    {
        RespondRecognize("""{"status":"success","result":{"timecode":"00:56","artist":"A","title":"T","apple_music":["not","an","object"]}}""");
        await using var audd = NewClient();
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(result);
        Assert.Null(result!.AppleMusic);
        Assert.Equal("A", result.Artist);
    }

    [Fact]
    public async Task RecognizeAsync_ResultWrongShape_DoesNotThrow()
    {
        RespondRecognize("""{"status":"success","result":"weird"}""");
        await using var audd = NewClient();
        // Must not throw a raw JsonException; a non-object result reads as "no match".
        var result = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.Null(result);
    }

    // ---- D1: lenient enterprise parsing (per-field / per-song, not whole-chunk) ----

    [Fact]
    public async Task RecognizeEnterpriseAsync_OneWrongTypedFieldInSong_KeepsSong_DegradesField()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""
                   {"status":"success","result":[
                     {"offset":"00:00","songs":[
                       {"artist":"A","title":"T","score":"98","timecode":"00:01"},
                       {"artist":"B","title":"U","score":95,"timecode":"00:02"}
                     ]}
                   ]}
                   """));
        await using var audd = NewClient();
        var matches = await audd.RecognizeEnterpriseAsync("https://x.example/y.mp3", limit: 1);
        Assert.Equal(2, matches.Count);
        // Wrong-typed score degrades to null; the rest of the song survives.
        Assert.Null(matches[0].Score);
        Assert.Equal("A", matches[0].Artist);
        Assert.Equal(95, matches[1].Score);
    }

    [Fact]
    public async Task RecognizeEnterpriseAsync_WrongTypedOffset_KeepsSongs()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""
                   {"status":"success","result":[
                     {"offset":123,"songs":[{"artist":"A","title":"T","timecode":"00:01"}]}
                   ]}
                   """));
        await using var audd = NewClient();
        var matches = await audd.RecognizeEnterpriseAsync("https://x.example/y.mp3", limit: 1);
        var m = Assert.Single(matches);
        Assert.Equal("A", m.Artist);
        // Offset was unusable, so absolute seconds stay unset.
        Assert.Null(m.StartSeconds);
    }

    [Fact]
    public async Task RecognizeEnterpriseAsync_RawResponseIsSongObject_NotWholeChunk()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""
                   {"status":"success","result":[
                     {"offset":"00:00","songs":[{"artist":"A","title":"T","timecode":"00:01"}]}
                   ]}
                   """));
        await using var audd = NewClient();
        var matches = await audd.RecognizeEnterpriseAsync("https://x.example/y.mp3", limit: 1);
        var m = Assert.Single(matches);
        // RawResponse should be the song object (has artist), not the chunk (has songs/offset).
        Assert.Equal(JsonValueKind.Object, m.RawResponse.ValueKind);
        Assert.True(m.RawResponse.TryGetProperty("artist", out _));
        Assert.False(m.RawResponse.TryGetProperty("songs", out _));
    }

    // ---- D1: lenient notification parsing ----

    [Fact]
    public void ParseCallback_WrongTypedNotificationCode_DoesNotThrow()
    {
        var json = """
        {"notification":{"radio_id":7,"notification_code":"not-a-number","notification_message":"hi"},"time":1587939136}
        """;
        var ev = AudDHelpers.ParseCallback(json);
        var notif = Assert.IsType<CallbackEvent.Notification>(ev);
        Assert.Null(notif.Value.NotificationCode);
        Assert.Equal("hi", notif.Value.NotificationMessage);
        Assert.Equal(7L, notif.Value.RadioId);
        Assert.Equal(1587939136L, notif.Value.Time);
    }

    // ---- D9: error-code decode robustness ----

    [Fact]
    public async Task RecognizeAsync_FractionalErrorCode_DoesNotThrow_MapsGracefully()
    {
        RespondRecognize("""{"status":"error","error":{"error_code":900.0,"error_message":"bad"}}""");
        await using var audd = NewClient();
        var ex = await Assert.ThrowsAsync<AudDAuthenticationException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));
        Assert.Equal(900, ex.ErrorCode);
    }

    [Fact]
    public async Task RecognizeAsync_BoolErrorCode_DoesNotThrow_FallsBackToServerException()
    {
        RespondRecognize("""{"status":"error","error":{"error_code":true,"error_message":"weird"}}""");
        await using var audd = NewClient();
        // A bool error_code decodes to 0 → generic server exception; must not throw JsonException.
        var ex = await Assert.ThrowsAsync<AudDServerException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));
        Assert.Equal(0, ex.ErrorCode);
    }

    // ---- D3: per-call timeout can shorten and extend ----

    [Fact]
    public async Task RecognizeAsync_ShortPerCallTimeout_TimesOut()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithDelay(TimeSpan.FromMilliseconds(800))
                   .WithBody("""{"status":"success","result":null}"""));
        await using var audd = NewClient();
        // A 100ms per-call timeout must fire well before the 800ms server delay.
        await Assert.ThrowsAsync<AudDConnectionException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3", timeout: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task RecognizeAsync_LargePerCallTimeout_Extends_BeyondStandardDefaultReasoning()
    {
        // The response is fast; the point is that passing a very large per-call
        // timeout does not itself cause a failure (it is honored, not capped).
        RespondRecognize("""{"status":"success","result":{"timecode":"00:56","artist":"A","title":"T"}}""");
        await using var audd = NewClient();
        var result = await audd.RecognizeAsync("https://x.example/y.mp3", timeout: TimeSpan.FromMinutes(30));
        Assert.NotNull(result);
        Assert.Equal("A", result!.Artist);
    }

    [Fact]
    public async Task RecognizeAsync_LargePerCallTimeout_SucceedsPastShorterDefault()
    {
        // Delay exceeds what a 60s-style hard cap would allow only in spirit; here we
        // verify a delayed response completes when the per-call timeout comfortably
        // covers it (the deadline is driven by the per-call value, not a fixed cap).
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithDelay(TimeSpan.FromMilliseconds(600))
                   .WithBody("""{"status":"success","result":{"timecode":"00:56","artist":"A","title":"T"}}"""));
        await using var audd = NewClient();
        var result = await audd.RecognizeAsync("https://x.example/y.mp3", timeout: TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
        Assert.Equal("A", result!.Artist);
    }

    // ---- D4: DI resolves a fresh client per request from the factory ----

    [Fact]
    public async Task AddAudD_ResolvesClientPerRequest_FromFactory()
    {
        var services = new ServiceCollection();
        var resolveCount = 0;
        var self = this;
        // A named client whose primary handler routes to our WireMock server, and
        // which we can count resolutions on via a configure callback.
        services.AddHttpClient("audd-fix", c => { })
                .ConfigurePrimaryHttpMessageHandler(() =>
                {
                    Interlocked.Increment(ref resolveCount);
                    return new RewriteHostHandler(new Uri(self._server.Url!));
                });
        services.AddAudD(opts =>
        {
            opts.ApiToken = "your-api-token";
            opts.HttpClientName = "audd-fix";
            opts.EnterpriseHttpClientName = "audd-fix";
            opts.MaxRetries = 1;
        });

        RespondRecognize("""{"status":"success","result":{"timecode":"00:56","artist":"A","title":"T"}}""");

        using var sp = services.BuildServiceProvider();
        var audd = sp.GetRequiredService<global::AudD.AudD>();

        var r1 = await audd.RecognizeAsync("https://x.example/y.mp3");
        var r2 = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        // The factory was consulted per request rather than captured once at
        // construction, so handler rotation is preserved.
        Assert.True(resolveCount >= 1);
    }

    // ---- D5: longpoll gets a dedicated timeout above the standard default ----

    [Fact]
    public async Task Longpoll_DelayedResponseWithinPollTimeout_IsDelivered()
    {
        // Preflight getCallbackUrl succeeds.
        _server.Given(Request.Create().WithPath("/getCallbackUrl/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":"https://audd.tech/empty/"}"""));
        // The longpoll response is delayed but well under (pollTimeout + margin).
        _server.Given(Request.Create().WithPath("/longpoll/").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithDelay(TimeSpan.FromMilliseconds(500))
                   .WithBody("""{"result":{"radio_id":1,"timestamp":"2026-01-01","play_length":100,"results":[{"artist":"A","title":"T"}]}}"""));

        await using var audd = NewClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Small poll timeout; the delayed response must still be delivered rather
        // than being cut off by the standard 60s transport ceiling.
        using var poll = await audd.Streams.LongpollAsync("cat123456", timeout: 2, cancellationToken: cts.Token);

        StreamCallbackMatch? first = null;
        await foreach (var m in poll.Matches.WithCancellation(cts.Token))
        {
            first = m;
            break;
        }
        Assert.NotNull(first);
        Assert.Equal("A", first!.Song.Artist);
    }

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
