using AudD;

var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
var url = args.Length > 0 ? args[0] : "https://audd.tech/example.mp3";

await using var audd = new AudD.AudD(token);
var result = await audd.RecognizeAsync(url);
if (result is null)
{
    Console.WriteLine("No match.");
    return 1;
}

Console.WriteLine($"{result.Artist} - {result.Title}");
return 0;
