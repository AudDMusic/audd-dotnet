using System.Net;
using System.Text.Json;
using AudD;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace AudD.Tests.Unit;

public class V020PolishTests_FromEnvironment
{
    [Fact]
    public void FromEnvironment_UsesAudDApiTokenEnvVar()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "env-token-xyz");
            using var audd = global::AudD.AudD.FromEnvironment();
            Assert.Equal("env-token-xyz", audd.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void FromEnvironment_Missing_RaisesArgumentExceptionWithDashboardHint()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", null);
            var ex = Assert.Throws<ArgumentException>(() => global::AudD.AudD.FromEnvironment());
            Assert.Contains("dashboard.audd.io", ex.Message);
            Assert.Contains("AUDD_API_TOKEN", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void Constructor_NullToken_FallsBackToEnvVar()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "env-token");
            using var audd = new global::AudD.AudD(apiToken: null);
            Assert.Equal("env-token", audd.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void Constructor_EmptyToken_FallsBackToEnvVar()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "from-env");
            using var audd = new global::AudD.AudD(apiToken: "");
            Assert.Equal("from-env", audd.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void Constructor_ExplicitTokenWins()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "env-token");
            using var audd = new global::AudD.AudD("explicit-token");
            Assert.Equal("explicit-token", audd.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void Constructor_NoTokenAndNoEnv_Throws()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", null);
            Assert.Throws<ArgumentException>(() => new global::AudD.AudD(apiToken: null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }
}

public class V020PolishTests_StreamingUrls
{
    private static RecognitionResult Parse(string json) =>
        JsonSerializer.Deserialize<RecognitionResult>(json)!;

    [Fact]
    public void StreamingUrl_AppleMusic_DirectFromMetadata()
    {
        var json = """
        {
          "timecode":"00:00",
          "song_link":"https://www.youtube.com/watch?v=abc",
          "apple_music":{"url":"https://music.apple.com/song/123"}
        }
        """;
        var r = Parse(json);
        Assert.Equal("https://music.apple.com/song/123", r.StreamingUrl(StreamingProvider.AppleMusic));
    }

    [Fact]
    public void StreamingUrl_Spotify_DirectFromExternalUrls()
    {
        var json = """
        {
          "timecode":"00:00",
          "song_link":"https://www.youtube.com/watch?v=abc",
          "spotify":{"id":"sid","external_urls":{"spotify":"https://open.spotify.com/track/sid"}}
        }
        """;
        var r = Parse(json);
        Assert.Equal("https://open.spotify.com/track/sid", r.StreamingUrl(StreamingProvider.Spotify));
    }

    [Fact]
    public void StreamingUrl_Deezer_DirectFromLink()
    {
        var json = """
        {
          "timecode":"00:00",
          "song_link":"https://www.youtube.com/watch?v=abc",
          "deezer":{"id":42,"link":"https://www.deezer.com/track/42"}
        }
        """;
        var r = Parse(json);
        Assert.Equal("https://www.deezer.com/track/42", r.StreamingUrl(StreamingProvider.Deezer));
    }

    [Fact]
    public void StreamingUrl_Napster_DirectFromHrefExtras()
    {
        var json = """
        {
          "timecode":"00:00",
          "song_link":"https://www.youtube.com/watch?v=abc",
          "napster":{"id":"nid","href":"https://app.napster.com/track/nid"}
        }
        """;
        var r = Parse(json);
        Assert.Equal("https://app.napster.com/track/nid", r.StreamingUrl(StreamingProvider.Napster));
    }

    [Fact]
    public void StreamingUrl_FallbackToLisTnRedirect_WhenNoMetadata()
    {
        var json = """{"timecode":"00:00","song_link":"https://lis.tn/abc"}""";
        var r = Parse(json);
        Assert.Equal("https://lis.tn/abc?spotify", r.StreamingUrl(StreamingProvider.Spotify));
        Assert.Equal("https://lis.tn/abc?apple_music", r.StreamingUrl(StreamingProvider.AppleMusic));
        Assert.Equal("https://lis.tn/abc?deezer", r.StreamingUrl(StreamingProvider.Deezer));
        Assert.Equal("https://lis.tn/abc?napster", r.StreamingUrl(StreamingProvider.Napster));
        Assert.Equal("https://lis.tn/abc?youtube", r.StreamingUrl(StreamingProvider.YouTube));
    }

    [Fact]
    public void StreamingUrl_NoSongLink_NoMetadata_ReturnsNull()
    {
        var json = """{"timecode":"00:00","artist":"x","title":"y"}""";
        var r = Parse(json);
        Assert.Null(r.StreamingUrl(StreamingProvider.Spotify));
        Assert.Null(r.StreamingUrl(StreamingProvider.YouTube));
    }

    [Fact]
    public void StreamingUrl_NonListnSongLink_NoFallback()
    {
        var json = """{"timecode":"00:00","song_link":"https://www.youtube.com/watch?v=abc"}""";
        var r = Parse(json);
        Assert.Null(r.StreamingUrl(StreamingProvider.Spotify));
        Assert.Null(r.StreamingUrl(StreamingProvider.YouTube));
    }

    [Fact]
    public void StreamingUrls_ReturnsAllResolvableProviders()
    {
        var json = """
        {
          "timecode":"00:00",
          "song_link":"https://lis.tn/xyz",
          "apple_music":{"url":"https://music.apple.com/song/1"}
        }
        """;
        var r = Parse(json);
        var urls = r.StreamingUrls();
        Assert.Equal("https://music.apple.com/song/1", urls[StreamingProvider.AppleMusic]);
        Assert.Equal("https://lis.tn/xyz?spotify", urls[StreamingProvider.Spotify]);
        Assert.Equal("https://lis.tn/xyz?youtube", urls[StreamingProvider.YouTube]);
        Assert.Equal(5, urls.Count);
    }

    [Fact]
    public void StreamingUrls_NoLisTnNoMetadata_Empty()
    {
        var json = """{"timecode":"00:00","artist":"x","title":"y"}""";
        var r = Parse(json);
        Assert.Empty(r.StreamingUrls());
    }

    [Fact]
    public void PreviewUrl_AppleMusicPreviewsFirst()
    {
        var json = """
        {
          "timecode":"00:00",
          "apple_music":{"previews":[{"url":"https://am.preview/p.m4a"}]},
          "spotify":{"preview_url":"https://sp.preview/p.mp3"},
          "deezer":{"preview":"https://dz.preview/p.mp3"}
        }
        """;
        var r = Parse(json);
        Assert.Equal("https://am.preview/p.m4a", r.PreviewUrl());
    }

    [Fact]
    public void PreviewUrl_FallsThroughToSpotify_ThenDeezer()
    {
        var json1 = """
        {"timecode":"00:00","spotify":{"preview_url":"https://sp/p.mp3"},"deezer":{"preview":"https://dz/p.mp3"}}
        """;
        Assert.Equal("https://sp/p.mp3", Parse(json1).PreviewUrl());

        var json2 = """{"timecode":"00:00","deezer":{"preview":"https://dz/p.mp3"}}""";
        Assert.Equal("https://dz/p.mp3", Parse(json2).PreviewUrl());
    }

    [Fact]
    public void PreviewUrl_NoneAvailable_ReturnsNull()
    {
        var json = """{"timecode":"00:00","artist":"x","title":"y"}""";
        Assert.Null(Parse(json).PreviewUrl());
    }

    [Fact]
    public void EnterpriseMatch_StreamingUrl_LisTnOnly()
    {
        var json = """{"score":100,"timecode":"00:00","song_link":"https://lis.tn/E1"}""";
        var m = JsonSerializer.Deserialize<EnterpriseMatch>(json)!;
        Assert.Equal("https://lis.tn/E1?spotify", m.StreamingUrl(StreamingProvider.Spotify));
        Assert.Equal("https://lis.tn/E1?thumb", m.ThumbnailUrl);
        Assert.Equal(5, m.StreamingUrls().Count);
    }

    [Fact]
    public void EnterpriseMatch_NonLisTn_NoStreamingUrls()
    {
        var json = """{"score":100,"timecode":"00:00","song_link":"https://www.youtube.com/watch?v=abc"}""";
        var m = JsonSerializer.Deserialize<EnterpriseMatch>(json)!;
        Assert.Null(m.StreamingUrl(StreamingProvider.Spotify));
        Assert.Empty(m.StreamingUrls());
        Assert.Null(m.ThumbnailUrl);
    }
}

public class V020PolishTests_SetApiToken
{
    [Fact]
    public void SetApiToken_RotatesObservableTokenProperty()
    {
        using var audd = new global::AudD.AudD("orig");
        Assert.Equal("orig", audd.ApiToken);
        audd.SetApiToken("new");
        Assert.Equal("new", audd.ApiToken);
    }

    [Fact]
    public void SetApiToken_NullOrEmpty_ThrowsArgumentException()
    {
        using var audd = new global::AudD.AudD("orig");
        Assert.Throws<ArgumentException>(() => audd.SetApiToken(null!));
        Assert.Throws<ArgumentException>(() => audd.SetApiToken(""));
        Assert.Equal("orig", audd.ApiToken);
    }

    [Fact]
    public void SetApiToken_AffectsStreamsDeriveLongpollCategory()
    {
        using var audd = new global::AudD.AudD("orig");
        var c1 = audd.Streams.DeriveLongpollCategory(1);
        audd.SetApiToken("new");
        var c2 = audd.Streams.DeriveLongpollCategory(1);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public async Task SetApiToken_ConcurrentRotations_AllSucceed()
    {
        using var audd = new global::AudD.AudD("token-0");
        var tasks = new List<Task>();
        for (int i = 1; i <= 50; i++)
        {
            int captured = i;
            tasks.Add(Task.Run(() => audd.SetApiToken($"token-{captured}")));
        }
        await Task.WhenAll(tasks);
        // Final value is one of token-1..token-50; never null/empty.
        Assert.False(string.IsNullOrEmpty(audd.ApiToken));
        Assert.StartsWith("token-", audd.ApiToken);
    }
}

public class V020PolishTests_OnEvent : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _client;

    public V020PolishTests_OnEvent()
    {
        var handler = new V020RewriteHostHandler(new Uri(_server.Url!));
        _client = new HttpClient(handler);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task OnEvent_RecognizeSuccess_EmitsRequestThenResponse()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithHeader("X-Request-Id", "req-99")
                   .WithBody("""{"status":"success","result":{"timecode":"00:00","artist":"a","title":"t"}}"""));

        var events = new List<AudDEvent>();
        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client,
            onEvent: events.Add);
        var r = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.NotNull(r);

        Assert.Equal(2, events.Count);
        Assert.Equal(AudDEventKind.Request, events[0].Kind);
        Assert.Equal("recognize", events[0].Method);
        Assert.Equal(AudDEventKind.Response, events[1].Kind);
        Assert.Equal("recognize", events[1].Method);
        Assert.Equal(200, events[1].HttpStatus);
        Assert.Equal("req-99", events[1].RequestId);
        Assert.NotNull(events[1].Elapsed);
    }

    [Fact]
    public async Task OnEvent_NeverContainsApiTokenInUrl()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":null}"""));

        var events = new List<AudDEvent>();
        await using var audd = new global::AudD.AudD("super-secret-token-xyz",
            httpClient: _client, enterpriseHttpClient: _client, onEvent: events.Add);
        await audd.RecognizeAsync("https://x.example/y.mp3");

        Assert.NotEmpty(events);
        foreach (var e in events)
        {
            Assert.DoesNotContain("super-secret-token-xyz", e.Url);
            foreach (var kv in e.Extras)
            {
                Assert.DoesNotContain("super-secret-token-xyz", kv.Value?.ToString() ?? "");
            }
        }
    }

    [Fact]
    public async Task OnEvent_ApiError_EmitsExceptionWithErrorCodeAndStatus()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"error","error":{"error_code":900,"error_message":"bad token"}}"""));

        var events = new List<AudDEvent>();
        await using var audd = new global::AudD.AudD("bad-token",
            httpClient: _client, enterpriseHttpClient: _client, onEvent: events.Add);

        await Assert.ThrowsAsync<AudDAuthenticationException>(
            () => audd.RecognizeAsync("https://x.example/y.mp3"));

        var exc = events.Single(e => e.Kind == AudDEventKind.Exception);
        Assert.Equal("recognize", exc.Method);
        Assert.Equal(900, exc.ErrorCode);
        Assert.NotNull(exc.HttpStatus);
        Assert.Contains("exception_type", exc.Extras.Keys);
    }

    [Fact]
    public async Task OnEvent_StreamsAddEmitsEvents()
    {
        _server.Given(Request.Create().WithPath("/addStream/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":{}}"""));

        var events = new List<AudDEvent>();
        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client,
            onEvent: events.Add);
        await audd.Streams.AddAsync("twitch:foo", radioId: 1);

        Assert.Equal(2, events.Count);
        Assert.Equal("addStream", events[0].Method);
        Assert.Equal(AudDEventKind.Request, events[0].Kind);
        Assert.Equal(AudDEventKind.Response, events[1].Kind);
    }

    [Fact]
    public async Task OnEvent_HookExceptionsAreSwallowed_RequestStillSucceeds()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":null}"""));

        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client,
            onEvent: _ => throw new InvalidOperationException("user hook bug"));

        // Hook throws on every event. Request must still complete.
        var r = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.Null(r);
    }

    [Fact]
    public async Task OnEvent_DefaultIsOff_NoEventsObserved()
    {
        _server.Given(Request.Create().WithPath("/").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithHeader("Content-Type", "application/json")
                   .WithBody("""{"status":"success","result":null}"""));

        // No onEvent param at all — verifies the hook is genuinely off by default.
        await using var audd = new global::AudD.AudD("test", httpClient: _client, enterpriseHttpClient: _client);
        var r = await audd.RecognizeAsync("https://x.example/y.mp3");
        Assert.Null(r);
    }
}

internal sealed class V020RewriteHostHandler : DelegatingHandler
{
    private readonly Uri _target;
    public V020RewriteHostHandler(Uri target) : base(new HttpClientHandler())
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
