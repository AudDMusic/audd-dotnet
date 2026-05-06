// ScanAndRename: walk a folder, recognize each audio file, write tags + rename.
//
// Default mode is dry-run — pass --apply to actually mutate files on disk.
// Reads AUDD_API_TOKEN from the environment (the AudD constructor handles it).
using System.Collections.Concurrent;
using AudD;

var (rootPath, apply, concurrency) = ParseArgs(args);
if (rootPath is null)
{
    Console.Error.WriteLine(
        "usage: dotnet run --project examples/ScanAndRename -- <folder> [--apply] [--concurrency N]");
    return 2;
}
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"folder not found: {rootPath}");
    return 2;
}

var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".mp4", ".wav", ".aac",
};

var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
    .Where(p => audioExts.Contains(Path.GetExtension(p)))
    .ToList();

if (files.Count == 0)
{
    Console.WriteLine($"no audio files found under {rootPath}");
    return 0;
}

Console.WriteLine($"scanning {files.Count} file(s) under {rootPath}");
Console.WriteLine($"mode: {(apply ? "APPLY (will modify files on disk)" : "dry-run (no changes)")}");
Console.WriteLine($"concurrency: {concurrency}");
Console.WriteLine();

await using var audd = new AudD.AudD(apiToken: null);  // reads AUDD_API_TOKEN

using var gate = new SemaphoreSlim(concurrency);
var counters = new Counters();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var tasks = files.Select(path => ProcessAsync(audd, path, apply, gate, counters, cts.Token)).ToArray();
try { await Task.WhenAll(tasks); }
catch (OperationCanceledException) { Console.WriteLine("cancelled."); }

Console.WriteLine();
Console.WriteLine("---- summary ----");
Console.WriteLine($"  recognized:    {counters.Recognized}");
Console.WriteLine($"  no match:      {counters.NoMatch}");
Console.WriteLine($"  tagged:        {counters.Tagged}");
Console.WriteLine($"  renamed:       {counters.Renamed}");
Console.WriteLine($"  skipped:       {counters.Skipped} (collision or unchanged name)");
Console.WriteLine($"  errors:        {counters.Errors}");
return counters.Errors > 0 ? 1 : 0;


static (string? root, bool apply, int concurrency) ParseArgs(string[] argv)
{
    string? root = null;
    bool apply = false;
    int concurrency = 4;
    for (int i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        if (a == "--apply") apply = true;
        else if (a == "--concurrency" && i + 1 < argv.Length)
        {
            if (!int.TryParse(argv[++i], out concurrency) || concurrency < 1) concurrency = 4;
        }
        else if (!a.StartsWith("-") && root is null) root = a;
    }
    return (root, apply, concurrency);
}

static async Task ProcessAsync(
    AudD.AudD audd,
    string path,
    bool apply,
    SemaphoreSlim gate,
    Counters counters,
    CancellationToken ct)
{
    await gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        RecognitionResult? result;
        try
        {
            result = await audd.RecognizeAsync(new FileInfo(path), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (AudDApiException ex)
        {
            Interlocked.Increment(ref counters.Errors);
            Console.Error.WriteLine($"[err]   {Display(path)}: AudD #{ex.ErrorCode} {ex.ServerMessage}");
            return;
        }
        catch (AudDException ex)
        {
            Interlocked.Increment(ref counters.Errors);
            Console.Error.WriteLine($"[err]   {Display(path)}: {ex.Message}");
            return;
        }

        if (result is null)
        {
            Interlocked.Increment(ref counters.NoMatch);
            Console.WriteLine($"[--]    {Display(path)}: no match");
            return;
        }
        Interlocked.Increment(ref counters.Recognized);

        var artist = result.Artist ?? "";
        var title = result.Title ?? "";
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title))
        {
            Console.WriteLine($"[??]    {Display(path)}: matched but artist/title incomplete; skipping rename");
            return;
        }

        var ext = Path.GetExtension(path);
        var safeArtist = Sanitize(artist);
        var safeTitle = Sanitize(title);
        var newName = $"{safeArtist} - {safeTitle}{ext}";
        var dir = Path.GetDirectoryName(path) ?? ".";
        var newPath = Path.Combine(dir, newName);

        var sameName = string.Equals(Path.GetFileName(path), newName, StringComparison.Ordinal);
        var collision = !sameName && File.Exists(newPath);

        if (apply)
        {
            try
            {
                using (var tag = TagLib.File.Create(path))
                {
                    tag.Tag.Performers = new[] { artist };
                    tag.Tag.Title = title;
                    if (!string.IsNullOrEmpty(result.Album)) tag.Tag.Album = result.Album;
                    tag.Save();
                }
                Interlocked.Increment(ref counters.Tagged);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counters.Errors);
                Console.Error.WriteLine($"[err]   {Display(path)}: tag write failed: {ex.Message}");
                return;
            }

            if (sameName)
            {
                Console.WriteLine($"[ok]    {Display(path)}: tagged ({artist} - {title}); filename already correct");
            }
            else if (collision)
            {
                Interlocked.Increment(ref counters.Skipped);
                Console.WriteLine($"[skip]  {Display(path)}: target exists ({newName})");
            }
            else
            {
                try
                {
                    File.Move(path, newPath);
                    Interlocked.Increment(ref counters.Renamed);
                    Console.WriteLine($"[mv]    {Display(path)} -> {newName}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref counters.Errors);
                    Console.Error.WriteLine($"[err]   {Display(path)}: rename failed: {ex.Message}");
                }
            }
        }
        else
        {
            // Dry-run: report what would happen.
            if (sameName)
            {
                Console.WriteLine($"[dry]   {Display(path)}: would tag ({artist} - {title}); filename already correct");
            }
            else if (collision)
            {
                Interlocked.Increment(ref counters.Skipped);
                Console.WriteLine($"[dry]   {Display(path)}: would tag; rename target exists -> SKIP ({newName})");
            }
            else
            {
                Console.WriteLine($"[dry]   {Display(path)}: would tag and rename -> {newName}");
            }
        }
    }
    finally
    {
        gate.Release();
    }
}

static string Sanitize(string s)
{
    var bad = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
    var chars = s.Select(c => bad.Contains(c) ? '_' : c).ToArray();
    var cleaned = new string(chars).Trim();
    if (cleaned.Length > 200) cleaned = cleaned[..200].TrimEnd();
    return string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
}

static string Display(string path) => Path.GetFileName(path);

internal sealed class Counters
{
    public int Recognized;
    public int NoMatch;
    public int Tagged;
    public int Renamed;
    public int Skipped;
    public int Errors;
}
