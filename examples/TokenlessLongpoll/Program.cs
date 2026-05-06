// Tokenless consumer for browser/widget/extension use cases — carries no api_token.
// Whoever derived the category is responsible for ensuring the callback URL is set.
using AudD;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run --project examples/TokenlessLongpoll -- <category-9-chars>");
    return 2;
}
var category = args[0];

await using var consumer = new LongpollConsumer(category);
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var poll = consumer.Iterate(cancellationToken: cts.Token);

// Drain notifications + errors in parallel with matches.
var notificationTask = Task.Run(async () =>
{
    await foreach (var n in poll.Notifications.WithCancellation(cts.Token))
    {
        Console.WriteLine($"notification: {n.NotificationMessage}");
    }
});
var errorTask = Task.Run(async () =>
{
    await foreach (var err in poll.Errors.WithCancellation(cts.Token))
    {
        Console.Error.WriteLine($"longpoll error: {err.Message}");
    }
});

try
{
    await foreach (var m in poll.Matches.WithCancellation(cts.Token))
    {
        Console.WriteLine($"match: {m.Song.Artist} - {m.Song.Title}");
    }
}
catch (OperationCanceledException) { }
return 0;
