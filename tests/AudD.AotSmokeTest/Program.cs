// AOT smoke test. Exercises the SDK's public surface enough to ensure
// the source-generated JsonContext, response decoder, and helpers all
// link cleanly under PublishAot=true. We never make a real network call —
// the goal is to prove the binary builds and runs without IL2026/IL3050
// warnings (TreatWarningsAsErrors fails the build otherwise).
using System.Text.Json;
using AudD;

// 1. Helpers — hits AudDHelpers + DeriveLongpollCategory + ParseCallback.
var category = AudDHelpers.DeriveLongpollCategory("test", radioId: 12345);
Console.WriteLine($"category={category}");

// 2. Source-generated parse path — exercises AudDJsonContext.Default.StreamCallbackMatch.
//    Real callback wraps result body in {"result": {...}}.
const string body = """
{
  "result": {
    "radio_id": 1,
    "timestamp": "2026-05-05 12:00:00",
    "results": [
      {"artist": "Test", "title": "Smoke", "score": 100}
    ]
  }
}
""";
var ev = AudDHelpers.ParseCallback(body);
var match = (CallbackEvent.Match)ev;
Console.WriteLine($"is_match=true radio_id={match.Value.RadioId} song={match.Value.Song.Title} alts={match.Value.Alternatives.Count}");

// 3. Models — confirm record types + StreamingUrl helpers AOT-link.
var rec = new RecognitionResult
{
    Artist = "A",
    Title = "T",
    SongLink = "https://lis.tn/abc",
};
Console.WriteLine($"thumb={rec.ThumbnailUrl} spotify={rec.StreamingUrl(StreamingProvider.Spotify)}");

// 4. Construct an AudD client without making a network call.
//    Throws ArgumentException when env var is missing — proves construction path links.
try
{
    Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "smoke-test-token");
    using var audd = new AudD.AudD("smoke-test-token");
    Console.WriteLine($"client_token_len={audd.ApiToken.Length}");
}
finally
{
    Environment.SetEnvironmentVariable("AUDD_API_TOKEN", null);
}

Console.WriteLine("aot_smoke_ok");
return 0;
