// Demonstrates parsing a streams callback POST body — your HTTP framework wraps this.
using AudD;

const string sample = """
{
  "status":"success",
  "result":{
    "radio_id":7,
    "timestamp":"2020-04-13 10:31:43",
    "play_length":111,
    "results":[
      {"artist":"Alan Walker","title":"Live Fast","score":100,"song_link":"https://lis.tn/LiveFastPUBGM"}
    ]
  }
}
""";

// AudDHelpers.ParseCallback works on string, JsonElement, or via the
// HandleCallbackAsync(stream) overload from your webhook handler.
var ev = AudDHelpers.ParseCallback(sample);
switch (ev)
{
    case CallbackEvent.Match m:
        Console.WriteLine($"Recognized stream {m.Value.RadioId}: {m.Value.Song.Artist} - {m.Value.Song.Title}");
        foreach (var alt in m.Value.Alternatives)
        {
            // Alternatives may carry a different artist/title — variant catalog releases.
            Console.WriteLine($"  alt: {alt.Artist} - {alt.Title}");
        }
        break;
    case CallbackEvent.Notification n:
        Console.WriteLine($"Notification: {n.Value.NotificationCode} {n.Value.NotificationMessage}");
        break;
}

// In an ASP.NET Core webhook handler:
//
//   app.MapPost("/audd-callback", async (HttpRequest req, AudD.AudD audd) =>
//   {
//       var ev = await audd.Streams.HandleCallbackAsync(req.Body);
//       // pattern-match as above
//       return Results.Ok();
//   });
