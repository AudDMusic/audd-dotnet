// Demonstrates parsing a streams callback POST body — your HTTP framework wraps this.
using AudD;

var sample = """
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

var payload = AudDHelpers.ParseCallback(sample);
if (payload.IsResult)
{
    Console.WriteLine($"Recognized stream {payload.Result!.RadioId}: {payload.Result.Results[0].Artist} - {payload.Result.Results[0].Title}");
}
else if (payload.IsNotification)
{
    Console.WriteLine($"Notification: {payload.Notification!.NotificationCode} {payload.Notification.NotificationMessage}");
}
