using AudD;

var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
var url = args.Length > 0 ? args[0] : "https://audd.tech/example.mp3";

await using var audd = new AudD.AudD(token);
// limit=1 keeps this example cheap: it caps the enterprise endpoint to a single match.
var matches = await audd.RecognizeEnterpriseAsync(url, limit: 1);
if (matches.Count == 0)
{
    Console.WriteLine("No matches.");
    return 1;
}
foreach (var m in matches)
{
    Console.WriteLine($"@{m.Timecode} score={m.Score} {m.Artist} - {m.Title} ISRC={m.Isrc} UPC={m.Upc}");
}
return 0;
