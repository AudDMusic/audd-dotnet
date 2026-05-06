using AudD;

// Token-bound longpoll: derive the category from your api_token + radio_id.
var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
long radioId = args.Length > 0 ? long.Parse(args[0]) : 1;

await using var audd = new AudD.AudD(token);
var category = audd.Streams.DeriveLongpollCategory(radioId);
Console.WriteLine($"Subscribing to category {category} (token-bound).");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await foreach (var ev in audd.Streams.LongpollAsync(category, cancellationToken: cts.Token))
    {
        Console.WriteLine($"event: {ev}");
    }
}
catch (OperationCanceledException) { }
