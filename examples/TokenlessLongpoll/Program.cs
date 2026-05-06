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

try
{
    await foreach (var ev in consumer.IterateAsync(cancellationToken: cts.Token))
    {
        Console.WriteLine($"event: {ev}");
    }
}
catch (OperationCanceledException) { }
return 0;
