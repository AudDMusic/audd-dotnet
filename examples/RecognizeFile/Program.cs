using AudD;

var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN") ?? "test";
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run --project examples/RecognizeFile -- <path-to-audio-file>");
    return 2;
}

await using var audd = new AudD.AudD(token);
var result = await audd.RecognizeAsync(new FileInfo(args[0]));
if (result is null)
{
    Console.WriteLine("No match.");
    return 1;
}
Console.WriteLine($"{result.Artist} - {result.Title} (timecode {result.Timecode})");
return 0;
