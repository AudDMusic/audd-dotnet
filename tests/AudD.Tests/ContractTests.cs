using System.Text.Json;
using AudD;
using AudD.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudD.Tests;

/// <summary>
/// Contract tests against shared OpenAPI fixtures. Verifies the .NET parser
/// produces the right typed object for each canonical sample. Fixtures live
/// in audd-openapi/fixtures and are loaded via the AUDD_OPENAPI_FIXTURES env
/// var, defaulting to a relative path that works from the test bin dir.
///
/// Tagged Category=Contract — the standard CI test step filters these out
/// (the audd-openapi sibling repo isn't checked out there); the dedicated
/// contract.yml workflow checks out the spec and runs only this category.
/// </summary>
[Trait("Category", "Contract")]
public class ContractTests
{
    private static string FixturesDir
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("AUDD_OPENAPI_FIXTURES");
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;
            // tests run from bin/.../net8.0/. Walk up to repo root and find audd-openapi/fixtures.
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(dir, "audd-openapi", "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
            throw new DirectoryNotFoundException("Could not locate audd-openapi/fixtures. Set AUDD_OPENAPI_FIXTURES.");
        }
    }

    private static JsonElement Load(string filename)
    {
        var path = Path.Combine(FixturesDir, filename);
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RecognizeBasic_Parses()
    {
        var body = Load("recognize_basic.json");
        Assert.True(body.TryGetProperty("result", out var r));
        var rec = r.Deserialize<RecognitionResult>(JsonOpts.Default);
        Assert.NotNull(rec);
        Assert.Equal("Tears For Fears", rec!.Artist);
        Assert.Equal("Everybody Wants To Rule The World", rec.Title);
        Assert.Equal("00:56", rec.Timecode);
        Assert.Equal("https://lis.tn/NbkVb?thumb", rec.ThumbnailUrl);
        Assert.True(rec.IsPublicMatch);
        Assert.False(rec.IsCustomMatch);
    }

    [Fact]
    public void RecognizeWithMetadata_AppleMusicAndSpotifyParse()
    {
        var body = Load("recognize_with_metadata.json");
        var r = body.GetProperty("result").Deserialize<RecognitionResult>(JsonOpts.Default)!;
        Assert.NotNull(r.AppleMusic);
        Assert.Equal("Tears for Fears", r.AppleMusic!.ArtistName);
        Assert.Equal("GBUM71403885", r.AppleMusic.Isrc);
        Assert.NotNull(r.Spotify);
        Assert.Equal("5B9qVIyjqeWkeOAp2tJgqL", r.Spotify!.Id);
        Assert.NotNull(r.MusicBrainz);
        Assert.NotEmpty(r.MusicBrainz!);
        // forward-compat: many extra fields on AppleMusic ('artwork', 'previews', etc.) get stashed:
        Assert.True(r.AppleMusic.Extras.ContainsKey("artwork"));
        Assert.True(r.AppleMusic.Extras.ContainsKey("previews"));
    }

    [Fact]
    public void RecognizeCustomMatch_HasAudioId()
    {
        var body = Load("recognize_custom_match.json");
        var r = body.GetProperty("result").Deserialize<RecognitionResult>(JsonOpts.Default)!;
        Assert.True(r.IsCustomMatch);
        Assert.Equal(146L, r.AudioId);
        Assert.Null(r.ThumbnailUrl);
    }

    [Fact]
    public void EnterpriseWithIsrcUpc_Parses()
    {
        var body = Load("enterprise_with_isrc_upc.json");
        var arr = body.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var chunks = new List<EnterpriseMatch>();
        foreach (var c in arr.EnumerateArray())
        {
            var chunk = c.Deserialize<EnterpriseChunkResult>(JsonOpts.Default)!;
            chunks.AddRange(chunk.Songs);
        }
        Assert.Single(chunks);
        Assert.Equal("GBUM71403885", chunks[0].Isrc);
        Assert.Equal("00602547037169", chunks[0].Upc);
    }

    [Fact]
    public void GetStreamsEmpty_ReturnsEmptyList()
    {
        var body = Load("getStreams_empty.json");
        var r = body.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, r.ValueKind);
        Assert.Equal(0, r.GetArrayLength());
    }

    [Fact]
    public void Error900_RaisesAuthenticationException()
    {
        var body = Load("error_900_invalid_token.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        Assert.IsType<AudDAuthenticationException>(exc);
        Assert.Equal(900, exc.ErrorCode);
        Assert.Contains("authorization failed", exc.ServerMessage);
        Assert.Equal("recognize", exc.RequestMethod);
    }

    [Fact]
    public void Error904Enterprise_RaisesSubscriptionException()
    {
        var body = Load("error_904_enterprise_unauthorized.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        Assert.IsType<AudDSubscriptionException>(exc);
        Assert.Equal(904, exc.ErrorCode);
        Assert.True(exc.RequestedParams.ContainsKey("limit"));
    }

    [Fact]
    public void Error904CustomCatalogContext_RaisesOverridden()
    {
        var body = Load("error_904_enterprise_unauthorized.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null, customCatalogContext: true);
        var cc = Assert.IsType<AudDCustomCatalogAccessException>(exc);
        Assert.Contains("Adding songs to your custom catalog", cc.Message);
    }

    [Fact]
    public void Error902StreamLimit_RaisesQuotaException()
    {
        var body = Load("error_902_stream_limit.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        Assert.IsType<AudDQuotaException>(exc);
        Assert.Equal(902, exc.ErrorCode);
    }

    [Fact]
    public void Error19NoCallback_RaisesBlocked_Code19()
    {
        var body = Load("error_19_no_callback_url.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        Assert.IsType<AudDBlockedException>(exc);
        Assert.Equal(19, exc.ErrorCode);
    }

    [Fact]
    public void Error700NoFile_RaisesInvalidRequest()
    {
        var body = Load("error_700_no_file.json");
        var exc = ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        Assert.IsType<AudDInvalidRequestException>(exc);
        Assert.Equal(700, exc.ErrorCode);
    }

    [Fact]
    public void StreamsCallbackResult_Parses()
    {
        var body = Load("streams_callback_with_result.json");
        var ev = AudDHelpers.ParseCallback(body);
        var match = Assert.IsType<CallbackEvent.Match>(ev);
        Assert.Equal(7L, match.Value.RadioId);
        Assert.Equal(111L, match.Value.PlayLength);
        Assert.Equal("Alan Walker, A$AP Rocky", match.Value.Song.Artist);
        Assert.Empty(match.Value.Alternatives);
    }

    [Fact]
    public void StreamsCallbackNotification_Parses()
    {
        var body = Load("streams_callback_with_notification.json");
        var ev = AudDHelpers.ParseCallback(body);
        var notif = Assert.IsType<CallbackEvent.Notification>(ev);
        Assert.Equal(3L, notif.Value.RadioId);
        Assert.Equal(650, notif.Value.NotificationCode);
        Assert.Equal(1587939136L, notif.Value.Time);
    }

    [Fact]
    public void LongpollNoEvents_HasTimeoutKey()
    {
        var body = Load("longpoll_no_events.json");
        Assert.True(body.TryGetProperty("timeout", out var t));
        Assert.Equal("no events before timeout", t.GetString());
    }
}
