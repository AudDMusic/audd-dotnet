using AudD;

var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
await using var audd = new AudD.AudD(token);

await audd.Streams.SetCallbackUrlAsync("https://audd.tech/empty/");
Console.WriteLine($"Callback URL: {await audd.Streams.GetCallbackUrlAsync()}");

await audd.Streams.AddAsync("https://npr-ice.streamguys1.com/live.mp3", radioId: 999001);
foreach (var s in await audd.Streams.ListAsync())
{
    Console.WriteLine($"Stream {s.RadioId}: {s.Url} (running={s.StreamRunning})");
}
