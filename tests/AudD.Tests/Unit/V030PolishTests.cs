using System.Net.Http;
using System.Text.Json;
using AudD;
using AudD.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Tests for the v0.3.0 polish: System.Text.Json source-generated metadata
/// (<see cref="AudDJsonContext"/>) and the <see cref="AudDServiceCollectionExtensions"/>
/// DI hookup.
/// </summary>
public class V030PolishTests_SourceGenContext
{
    [Fact]
    public void AudDJsonContext_HasMetadataForRecognitionResult()
    {
        var info = AudDJsonContext.Default.RecognitionResult;
        Assert.NotNull(info);
        Assert.Equal(typeof(RecognitionResult), info.Type);
    }

    [Fact]
    public void AudDJsonContext_HasMetadataForEnterpriseChunkResult()
    {
        var info = AudDJsonContext.Default.EnterpriseChunkResult;
        Assert.NotNull(info);
        Assert.Equal(typeof(EnterpriseChunkResult), info.Type);
    }

    [Fact]
    public void AudDJsonContext_DeserializeRecognitionResult_RoundTripsAllKnownFields()
    {
        const string body = """
        {
          "artist": "AC/DC",
          "title": "Thunderstruck",
          "album": "The Razors Edge",
          "release_date": "1990-09-24",
          "label": "Atco",
          "timecode": "00:42",
          "song_link": "https://lis.tn/abc",
          "spotify": {
            "id": "spotify-id",
            "name": "Thunderstruck",
            "duration_ms": 292000,
            "explicit": false,
            "external_urls": {"spotify": "https://open.spotify.com/track/abc"}
          }
        }
        """;
        using var doc = JsonDocument.Parse(body);
        var parsed = doc.RootElement.Deserialize(AudDJsonContext.Default.RecognitionResult);
        Assert.NotNull(parsed);
        Assert.Equal("AC/DC", parsed!.Artist);
        Assert.Equal("Thunderstruck", parsed.Title);
        Assert.Equal("00:42", parsed.Timecode);
        Assert.Equal("spotify-id", parsed.Spotify?.Id);
        // external_urls lands in Spotify.Extras (forward-compat, set-mode now).
        Assert.True(parsed.Spotify!.Extras.ContainsKey("external_urls"));
        // Direct streaming URL resolves through the source-gen-deserialized object.
        Assert.Equal("https://open.spotify.com/track/abc", parsed.StreamingUrl(StreamingProvider.Spotify));
    }

    [Fact]
    public void AudDJsonContext_DeserializeStreamCallbackMatch_HandlesTopLevelExtras()
    {
        const string body = """
        {
          "radio_id": 99,
          "timestamp": "2026-05-05 12:00:00",
          "play_length": 180,
          "results": [
            {"artist": "X", "title": "Y", "score": 100}
          ],
          "future_field": "ignore-me"
        }
        """;
        using var doc = JsonDocument.Parse(body);
        var parsed = doc.RootElement.Deserialize(AudDJsonContext.Default.StreamCallbackMatch);
        Assert.NotNull(parsed);
        Assert.Equal(99, parsed!.RadioId);
        Assert.Equal(180, parsed.PlayLength);
        Assert.Equal("X", parsed.Song.Artist);
        Assert.Empty(parsed.Alternatives);
        Assert.True(parsed.Extras.ContainsKey("future_field"));
    }

    [Fact]
    public void JsonOpts_GetTypeInfo_ResolvesViaSourceGenerator()
    {
        // Once JsonSerializerOptions is locked (after first use), TypeInfoResolver
        // may be migrated to a chain. Verify resolution by GetTypeInfo() instead —
        // a reflection-fallback regression would not return a Source-generated
        // OriginatingResolver pointing at AudDJsonContext.
        var info = JsonOpts.Default.GetTypeInfo(typeof(RecognitionResult));
        Assert.NotNull(info);
        Assert.Equal(typeof(RecognitionResult), info.Type);
        Assert.Same(AudDJsonContext.Default, info.OriginatingResolver);
    }
}

public class V030PolishTests_DependencyInjection
{
    [Fact]
    public void AddAudD_RegistersAudDAsSingleton_WithExplicitToken()
    {
        var services = new ServiceCollection();
        services.AddAudD(opts => opts.ApiToken = "di-test-token");
        using var sp = services.BuildServiceProvider();

        var audd = sp.GetRequiredService<global::AudD.AudD>();
        Assert.Equal("di-test-token", audd.ApiToken);
        Assert.Equal(3, audd.MaxRetries);

        // Singleton — same instance across resolutions.
        var second = sp.GetRequiredService<global::AudD.AudD>();
        Assert.Same(audd, second);
    }

    [Fact]
    public void AddAudD_BindsAudDOptions_FromIConfiguration()
    {
        // Use a hand-rolled provider to exercise the IConfiguration.Bind code path
        // end-to-end without taking a runtime dep on
        // Microsoft.Extensions.Configuration.Memory in the test harness.
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AudD:ApiToken"] = "config-token-xyz",
            ["AudD:MaxRetries"] = "5",
            ["AudD:BackoffFactor"] = "1.5",
        };
        IConfiguration config = new ConfigurationBuilder()
            .Add(new TestConfigSource(data))
            .Build();

        var services = new ServiceCollection();
        services.AddAudD(opts => config.GetSection("AudD").Bind(opts));
        using var sp = services.BuildServiceProvider();

        var audd = sp.GetRequiredService<global::AudD.AudD>();
        Assert.Equal("config-token-xyz", audd.ApiToken);
        Assert.Equal(5, audd.MaxRetries);
        Assert.Equal(1.5, audd.BackoffFactor);
    }

    /// <summary>
    /// Minimal IConfigurationSource backed by a flat dictionary. Equivalent to
    /// AddInMemoryCollection(...) for our needs but without taking the
    /// Microsoft.Extensions.Configuration.Memory dependency.
    /// </summary>
    private sealed class TestConfigSource : IConfigurationSource
    {
        private readonly IDictionary<string, string?> _data;
        public TestConfigSource(IDictionary<string, string?> data) { _data = data; }
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new TestConfigProvider(_data);
    }

    private sealed class TestConfigProvider : ConfigurationProvider
    {
        public TestConfigProvider(IDictionary<string, string?> data)
        {
            // ConfigurationProvider.Data is a Dictionary<string,string?> with the
            // OrdinalIgnoreCase comparer baked in; copy our flat keys verbatim.
            foreach (var kvp in data) Data[kvp.Key] = kvp.Value;
        }
    }

    [Fact]
    public void AddAudD_PrefersIHttpClientFactory_WhenRegistered()
    {
        var services = new ServiceCollection();
        // Custom named client. AddAudD's call to AddHttpClient() must not stomp this.
        services.AddHttpClient("audd-client", c =>
        {
            c.DefaultRequestHeaders.TryAddWithoutValidation("X-Custom", "ok");
        });
        services.AddAudD(opts =>
        {
            opts.ApiToken = "factory-test";
            opts.HttpClientName = "audd-client";
        });
        using var sp = services.BuildServiceProvider();

        // Resolution must succeed — factory wiring is the load-bearing piece here.
        var audd = sp.GetRequiredService<global::AudD.AudD>();
        Assert.NotNull(audd);
        Assert.Equal("factory-test", audd.ApiToken);

        // The factory itself was preserved; named client was NOT replaced by AddAudD().
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var named = factory.CreateClient("audd-client");
        Assert.True(named.DefaultRequestHeaders.TryGetValues("X-Custom", out var values));
        Assert.Contains("ok", values);
    }

    [Fact]
    public void AddAudD_WithOnEvent_WiresInspectionHook()
    {
        var captured = new List<AudDEvent>();
        var services = new ServiceCollection();
        services.AddAudD(opts => opts.ApiToken = "hook-test")
                .WithOnEvent(evt => captured.Add(evt));
        using var sp = services.BuildServiceProvider();

        // Force the hook holder to materialize alongside AudD.
        var audd = sp.GetRequiredService<global::AudD.AudD>();
        Assert.NotNull(audd);
        // We don't make a network call here — just confirm the pipeline registered
        // without error. (Live event-emission paths are covered by V020PolishTests
        // and AudDClientHttpTests via WireMock.)
        Assert.Empty(captured);
    }

    [Fact]
    public void AudDOptions_DefaultValues_MatchClientDefaults()
    {
        var opts = new AudDOptions();
        Assert.Equal(3, opts.MaxRetries);
        Assert.Equal(0.5, opts.BackoffFactor);
        Assert.Null(opts.ApiToken);
        Assert.Null(opts.HttpClientName);
        Assert.Null(opts.EnterpriseHttpClientName);
    }

    [Fact]
    public void AddAudD_FallsBackToEnvVar_WhenApiTokenNotSet()
    {
        var prior = Environment.GetEnvironmentVariable("AUDD_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "env-fallback-via-di");
            var services = new ServiceCollection();
            services.AddAudD(); // no configure callback
            using var sp = services.BuildServiceProvider();

            var audd = sp.GetRequiredService<global::AudD.AudD>();
            Assert.Equal("env-fallback-via-di", audd.ApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUDD_API_TOKEN", prior);
        }
    }

    [Fact]
    public void AddAudD_ResolvesAudDOptionsThroughIOptions()
    {
        var services = new ServiceCollection();
        services.AddAudD(opts => opts.ApiToken = "options-pattern");
        using var sp = services.BuildServiceProvider();

        var resolved = sp.GetRequiredService<IOptions<AudDOptions>>().Value;
        Assert.Equal("options-pattern", resolved.ApiToken);
    }
}
