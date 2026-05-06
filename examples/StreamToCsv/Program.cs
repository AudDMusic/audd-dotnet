// StreamToCsv: longpoll an AudD stream and append every recognized song to a CSV.
//
// Two modes:
//   --url URL [--radio-id N]      provision a stream, listen, delete on exit
//   --radio-id N                  listen-only against an existing slot (no add/delete)
//
// AUDD_API_TOKEN is read from the environment by the AudD constructor.
using System.Globalization;
using System.Text;
using System.Text.Json;
using AudD;

const string EmptyCallbackUrl = "https://audd.tech/empty/";
const long DefaultProvisionRadioId = 99999;

var opts = ParseArgs(args);
if (opts is null)
{
    PrintUsage();
    return 2;
}

await using var audd = new AudD.AudD(apiToken: null);

var cts = new CancellationTokenSource();
var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    shutdown.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    cts.Cancel();
    shutdown.TrySetResult();
};

bool weAddedTheStream = false;
long radioId = opts.RadioId ?? DefaultProvisionRadioId;
bool weTouchedCallbackUrl = false;

try
{
    // ---- Callback-URL handling --------------------------------------
    if (opts.Url is not null)
    {
        // Mode 1: provision-and-listen. Try to read the existing callback URL;
        // if the account has none configured (#19), set audd.tech/empty/ so
        // longpoll will deliver — that's a server-side requirement.
        try
        {
            var existing = await audd.Streams.GetCallbackUrlAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(existing))
            {
                await audd.Streams.SetCallbackUrlAsync(EmptyCallbackUrl, cancellationToken: cts.Token)
                    .ConfigureAwait(false);
                weTouchedCallbackUrl = true;
                Console.WriteLine($"longpoll requires any 200-OK URL server-side; using {EmptyCallbackUrl} as a default.");
            }
            else
            {
                Console.WriteLine($"using existing callback URL: {existing}");
            }
        }
        catch (AudDApiException ex) when (ex.ErrorCode == 19)
        {
            await audd.Streams.SetCallbackUrlAsync(EmptyCallbackUrl, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            weTouchedCallbackUrl = true;
            Console.WriteLine($"longpoll requires any 200-OK URL server-side; using {EmptyCallbackUrl} as a default.");
        }

        Console.WriteLine($"adding stream radio_id={radioId} url={opts.Url}");
        await audd.Streams.AddAsync(opts.Url, radioId, cancellationToken: cts.Token).ConfigureAwait(false);
        weAddedTheStream = true;
    }
    else
    {
        // Mode 2: listen-only. Refuse if no callback URL is configured —
        // longpoll won't deliver and the user needs to fix it deliberately.
        try
        {
            _ = await audd.Streams.GetCallbackUrlAsync(cts.Token).ConfigureAwait(false);
        }
        catch (AudDApiException ex) when (ex.ErrorCode == 19)
        {
            Console.Error.WriteLine(
                "stream slot exists but no callback URL is configured for this account; " +
                "longpoll won't deliver. Set one first via SetCallbackUrlAsync(...).");
            return 1;
        }
    }

    // ---- Open the CSV (append mode) ---------------------------------
    bool needHeader = !File.Exists(opts.Output) || new FileInfo(opts.Output).Length == 0;
    await using var csv = new StreamWriter(
        new FileStream(opts.Output, FileMode.Append, FileAccess.Write, FileShare.Read),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    csv.NewLine = "\n";
    if (needHeader)
    {
        await csv.WriteLineAsync("received_at,radio_id,timestamp,score,artist,title,album,song_link")
            .ConfigureAwait(false);
        await csv.FlushAsync().ConfigureAwait(false);
    }

    // ---- Subscribe and consume --------------------------------------
    var category = audd.Streams.DeriveLongpollCategory(radioId);
    Console.WriteLine($"longpolling category {category} (radio_id={radioId}) -> {opts.Output}");
    Console.WriteLine("press Ctrl-C to stop.");

    try
    {
        await foreach (var body in audd.Streams.LongpollAsync(
            category,
            skipCallbackCheck: true,  // we already preflighted above
            cancellationToken: cts.Token).ConfigureAwait(false))
        {
            await HandleEnvelopeAsync(audd, body, csv).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) { }
}
catch (AudDApiException ex)
{
    Console.Error.WriteLine($"AudD #{ex.ErrorCode}: {ex.ServerMessage}");
    return 1;
}
finally
{
    // Mode 1 only: best-effort delete of the stream we provisioned.
    if (weAddedTheStream)
    {
        try
        {
            using var deleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await audd.Streams.DeleteAsync(radioId, deleteCts.Token).ConfigureAwait(false);
            Console.WriteLine($"deleted stream radio_id={radioId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: failed to delete stream radio_id={radioId}: {ex.Message}");
        }
    }
    if (weTouchedCallbackUrl)
    {
        Console.WriteLine(
            $"left {EmptyCallbackUrl} as your account callback — change it via SetCallbackUrlAsync(...) if needed.");
    }
    shutdown.TrySetResult();
}

return 0;


static async Task HandleEnvelopeAsync(AudD.AudD audd, JsonElement body, StreamWriter csv)
{
    // The longpoll wraps multiple events in `events: []`. Older payloads put
    // a single event at the top level. Support both.
    if (body.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
    {
        foreach (var ev in events.EnumerateArray())
        {
            await HandleEnvelopeAsync(audd, ev, csv).ConfigureAwait(false);
        }
        return;
    }

    StreamCallbackPayload payload;
    try
    {
        payload = audd.Streams.ParseCallback(body);
    }
    catch
    {
        // Heartbeat / unrecognized envelope — ignore.
        return;
    }

    if (payload.IsNotification && payload.Notification is { } n)
    {
        Console.Error.WriteLine(
            $"notification radio_id={n.RadioId} code={n.NotificationCode} {n.NotificationMessage}");
        return;
    }
    if (!payload.IsResult || payload.Result is null) return;

    var r = payload.Result;
    var receivedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    foreach (var entry in r.Results)
    {
        var line = string.Join(",",
            CsvField(receivedAt),
            CsvField(r.RadioId.ToString(CultureInfo.InvariantCulture)),
            CsvField(r.Timestamp ?? ""),
            CsvField(entry.Score.ToString(CultureInfo.InvariantCulture)),
            CsvField(entry.Artist),
            CsvField(entry.Title),
            CsvField(entry.Album ?? ""),
            CsvField(entry.SongLink ?? ""));
        await csv.WriteLineAsync(line).ConfigureAwait(false);
        await csv.FlushAsync().ConfigureAwait(false);
        Console.WriteLine($"[{receivedAt}] radio_id={r.RadioId}: {entry.Artist} - {entry.Title}");
    }
}

static string CsvField(string s)
{
    var needsQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
    if (!needsQuote) return s;
    return "\"" + s.Replace("\"", "\"\"") + "\"";
}

static Options? ParseArgs(string[] argv)
{
    string? url = null;
    long? radioId = null;
    string output = "audd_stream_tracks.csv";
    for (int i = 0; i < argv.Length; i++)
    {
        var a = argv[i];
        switch (a)
        {
            case "--url" when i + 1 < argv.Length:
                url = argv[++i];
                break;
            case "--radio-id" when i + 1 < argv.Length:
                if (!long.TryParse(argv[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rid))
                    return null;
                radioId = rid;
                break;
            case "--output" when i + 1 < argv.Length:
                output = argv[++i];
                break;
            case "-h" or "--help":
                return null;
            default:
                Console.Error.WriteLine($"unknown argument: {a}");
                return null;
        }
    }
    if (url is null && radioId is null) return null;
    return new Options(url, radioId, output);
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        """
        usage:
          # provision a stream, listen, delete on exit
          dotnet run --project examples/StreamToCsv -- --url URL [--radio-id N] [--output FILE]

          # listen against an existing slot (does NOT add or delete)
          dotnet run --project examples/StreamToCsv -- --radio-id N [--output FILE]

        defaults: --radio-id 99999 (provision mode), --output audd_stream_tracks.csv
        """);
}

internal sealed record Options(string? Url, long? RadioId, string Output);
