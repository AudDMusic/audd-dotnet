using System.Text.Json;
using AudD;
using Xunit;

namespace AudD.Tests.Unit;

public class ModelsTests
{
    [Fact]
    public void RecognitionResult_PublicMatch_ParsesAndReportsTrue()
    {
        var json = """
        {
          "timecode":"00:56",
          "artist":"Tears For Fears",
          "title":"Everybody Wants To Rule The World",
          "song_link":"https://lis.tn/NbkVb"
        }
        """;
        var r = JsonSerializer.Deserialize<RecognitionResult>(json)!;
        Assert.False(r.IsCustomMatch);
        Assert.True(r.IsPublicMatch);
        Assert.Equal("Tears For Fears", r.Artist);
        Assert.Equal("https://lis.tn/NbkVb?thumb", r.ThumbnailUrl);
    }

    [Fact]
    public void RecognitionResult_CustomMatch_HasAudioId()
    {
        var json = """{"timecode":"01:45","audio_id":146}""";
        var r = JsonSerializer.Deserialize<RecognitionResult>(json)!;
        Assert.True(r.IsCustomMatch);
        Assert.False(r.IsPublicMatch);
        Assert.Equal(146L, r.AudioId);
        Assert.Null(r.ThumbnailUrl);
    }

    [Fact]
    public void RecognitionResult_NonListnSongLink_NoThumbnail()
    {
        var json = """{"timecode":"00:00","artist":"x","title":"y","song_link":"https://www.youtube.com/watch?v=abc"}""";
        var r = JsonSerializer.Deserialize<RecognitionResult>(json)!;
        Assert.Null(r.ThumbnailUrl);
    }

    [Fact]
    public void RecognitionResult_ThumbnailUrl_AppendsAmpWhenQueryExists()
    {
        var json = """{"timecode":"00:00","artist":"x","title":"y","song_link":"https://lis.tn/abc?id=1"}""";
        var r = JsonSerializer.Deserialize<RecognitionResult>(json)!;
        Assert.Equal("https://lis.tn/abc?id=1&thumb", r.ThumbnailUrl);
    }

    [Fact]
    public void Extras_CapturesUnknownFields()
    {
        var json = """{"timecode":"00:00","tidal":{"id":"future-field"}}""";
        var r = JsonSerializer.Deserialize<RecognitionResult>(json)!;
        Assert.True(r.Extras.ContainsKey("tidal"));
    }

    [Fact]
    public void StreamCallback_Match_ParsesViaCallbackEvent()
    {
        var json = """
        {
          "status": "success",
          "result": {
            "radio_id": 7,
            "timestamp": "2020-04-13 10:31:43",
            "play_length": 111,
            "results": [{"artist":"A","title":"T","score":100}]
          }
        }
        """;
        var ev = AudDHelpers.ParseCallback(json);
        var match = Assert.IsType<CallbackEvent.Match>(ev);
        Assert.Equal(7, match.Value.RadioId);
        Assert.Equal("A", match.Value.Song.Artist);
        Assert.Equal(100, match.Value.Song.Score);
        Assert.Empty(match.Value.Alternatives);
    }

    [Fact]
    public void StreamCallback_MultipleCandidates_SplitsIntoSongAndAlternatives()
    {
        var json = """
        {
          "result": {
            "radio_id": 9,
            "results": [
              {"artist":"Top","title":"Live","score":100},
              {"artist":"Variant","title":"Live (Remastered)","score":90},
              {"artist":"OtherArtist","title":"Different","score":75}
            ]
          }
        }
        """;
        var ev = AudDHelpers.ParseCallback(json);
        var match = Assert.IsType<CallbackEvent.Match>(ev);
        Assert.Equal("Top", match.Value.Song.Artist);
        Assert.Equal(2, match.Value.Alternatives.Count);
        // Documented: alternatives may carry different artist/title.
        Assert.Equal("OtherArtist", match.Value.Alternatives[1].Artist);
    }

    [Fact]
    public void StreamCallback_Notification_ParsesViaCallbackEvent()
    {
        var json = """
        {
          "status":"-",
          "notification":{"radio_id":3,"stream_running":false,"notification_code":650,"notification_message":"oops"},
          "time": 1587939136
        }
        """;
        var ev = AudDHelpers.ParseCallback(json);
        var notif = Assert.IsType<CallbackEvent.Notification>(ev);
        Assert.Equal(650, notif.Value.NotificationCode);
        Assert.Equal(1587939136L, notif.Value.Time);
    }
}
