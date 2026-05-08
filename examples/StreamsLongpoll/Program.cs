using AudD;

// Token-bound longpoll: derive the category from your api_token + radio_id.
var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
long radioId = args.Length > 0 ? long.Parse(args[0]) : 1;

await using var audd = new AudD.AudD(token);
var category = audd.Streams.DeriveLongpollCategory(radioId);
Console.WriteLine($"Subscribing to category {category} (token-bound).");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var poll = await audd.Streams.LongpollAsync(category, cancellationToken: cts.Token);

// Forward errors to the console; consumes the Errors stream concurrently with Matches.
var errorTask = Task.Run(async () =>
{
    await foreach (var err in poll.Errors.WithCancellation(cts.Token))
    {
        Console.Error.WriteLine($"longpoll error: {err.Message}");
    }
});
var notificationTask = Task.Run(async () =>
{
    await foreach (var n in poll.Notifications.WithCancellation(cts.Token))
    {
        Console.WriteLine($"notification radio_id={n.RadioId} code={n.NotificationCode}: {n.NotificationMessage}");
    }
});

try
{
    await foreach (var m in poll.Matches.WithCancellation(cts.Token))
    {
        Console.WriteLine($"match radio_id={m.RadioId}: {m.Song.Artist} - {m.Song.Title}");
    }
}
catch (OperationCanceledException) { }
