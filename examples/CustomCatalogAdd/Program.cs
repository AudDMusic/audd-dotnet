// WARNING: This is NOT how you submit audio for music recognition.
// Use RecognizeAsync / RecognizeEnterpriseAsync instead.
//
// This adds a song to YOUR private fingerprint database so AudD's recognition
// can later identify YOUR OWN tracks for YOUR account only. Requires special
// access — contact api@audd.io if you need it enabled.
using AudD;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run --project examples/CustomCatalogAdd -- <audio_id> <path-to-audio-file>");
    return 2;
}
var audioId = long.Parse(args[0]);
var path = args[1];

var token = Environment.GetEnvironmentVariable("AUDD_API_TOKEN")
            ?? throw new InvalidOperationException("Set AUDD_API_TOKEN — custom catalog requires a real account.");
await using var audd = new AudD.AudD(token);

try
{
    await audd.CustomCatalog.AddAsync(audioId, new FileInfo(path));
    Console.WriteLine($"Added audio_id={audioId}");
}
catch (AudDCustomCatalogAccessException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 4;
}
return 0;
