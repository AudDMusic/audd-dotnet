# audd-dotnet

[![CI](https://github.com/AudDMusic/audd-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/AudDMusic/audd-dotnet/actions/workflows/ci.yml)
[![Contract](https://github.com/AudDMusic/audd-dotnet/actions/workflows/contract.yml/badge.svg)](https://github.com/AudDMusic/audd-dotnet/actions/workflows/contract.yml)
[![NuGet](https://img.shields.io/nuget/v/AudD.svg)](https://www.nuget.org/packages/AudD)
[![NuGet downloads](https://img.shields.io/nuget/dt/AudD.svg)](https://www.nuget.org/packages/AudD)

Official .NET SDK for [music recognition API](https://audd.io): identify music from a short audio clip, a long audio file, or a live stream.

The API itself is so simple that it can easily be used even without an SDK: [docs.audd.io](https://docs.audd.io).

## Quickstart

```bash
dotnet add package AudD
```

Get your API token at [dashboard.audd.io](https://dashboard.audd.io).

Recognize from a URL:

```csharp
using AudD;

var audd = new AudD("your-api-token");
var result = await audd.RecognizeAsync("https://audd.tech/example.mp3");
if (result is not null)
{
    Console.WriteLine($"{result.Artist} — {result.Title}");
}
```

Recognize from a local file:

```csharp
using AudD;

var audd = new AudD("your-api-token");
var result = await audd.RecognizeAsync("/path/to/clip.mp3");
if (result is not null)
{
    Console.WriteLine($"{result.Artist} — {result.Title}");
}
```

`RecognizeAsync` is overloaded for `string` (URL or path), `FileInfo`, `Stream`, and `byte[]` — pick whichever fits your call site. It returns a `RecognitionResult?` — `null` when the clip isn't recognized.

For files longer than 25 seconds (broadcasts, podcasts, full DJ sets), use `RecognizeEnterpriseAsync(source, limit: ...)` — it returns `IReadOnlyList<EnterpriseMatch>`, one per song detected across the file.

## Authentication

Pass the token to the constructor:

```csharp
var audd = new AudD("your-token");
```

Or omit it and set `AUDD_API_TOKEN` in the environment — the SDK reads it on construction:

```csharp
Environment.SetEnvironmentVariable("AUDD_API_TOKEN", "your-token");
var audd = new AudD(apiToken: null);          // env-var fallback
// or, equivalent and more explicit:
var audd = AudD.FromEnvironment();
```

For long-running services that rotate tokens (e.g., from Azure Key Vault or AWS Secrets Manager), call `audd.SetApiToken(newToken)`. In-flight requests finish on the previous token; subsequent requests use the new one.

## What you get back

By default `RecognizeAsync` returns the core tags plus AudD's universal song link — no metadata-block opt-in needed:

```csharp
using AudD;

var audd = new AudD("your-token");
var result = await audd.RecognizeAsync("https://audd.tech/example.mp3");
if (result is null) return;

// Core tags
Console.WriteLine($"{result.Artist} — {result.Title}");
Console.WriteLine($"{result.Album} • {result.ReleaseDate} • {result.Label}");

// AudD's universal song page — links into every provider
Console.WriteLine(result.SongLink);

// Helpers — driven off SongLink, work without any @return opt-in
Console.WriteLine(result.ThumbnailUrl);                              // cover-art URL, or null
Console.WriteLine(result.StreamingUrl(StreamingProvider.Spotify));   // direct or lis.tn redirect, or null
foreach (var (provider, url) in result.StreamingUrls())              // every resolvable provider
{
    Console.WriteLine($"{provider}: {url}");
}
```

If you need provider-specific metadata blocks, opt in per call. Request only what you need — each provider you ask for adds latency:

```csharp
var result = await audd.RecognizeAsync(
    "https://audd.tech/example.mp3",
    @return: new[] { "apple_music", "spotify" });

Console.WriteLine(result?.AppleMusic?.Url);   // direct Apple Music link
Console.WriteLine(result?.Spotify?.Uri);      // spotify:track:...
Console.WriteLine(result?.PreviewUrl());      // first preview across requested providers, null if none
```

Valid `@return` values: `apple_music`, `spotify`, `deezer`, `napster`, `musicbrainz`. The corresponding properties (`AppleMusic`, `Spotify`, `Deezer`, `Napster`, `MusicBrainz`) stay `null` when not requested.

`EnterpriseMatch` (returned by `RecognizeEnterpriseAsync`) carries the same core tags plus `Score`, `StartOffset`, `EndOffset`, `Isrc`, `Upc`. Access to `Isrc`, `Upc`, and `Score` requires a Startup plan or higher — [contact us](mailto:api@audd.io) for enterprise features.

## Reading additional metadata

The typed records cover what AudD documents. To read undocumented or beta fields the server returns, go through `Extras`:

```csharp
// Top-level extras
if (result.Extras.TryGetValue("genre", out var genre))
{
    Console.WriteLine(genre.GetString());
}

// Nested extras inside a typed metadata block
if (result.AppleMusic is not null
    && result.AppleMusic.Extras.TryGetValue("artwork", out var artwork))
{
    Console.WriteLine(artwork);
}
```

`Extras` is an `IDictionary<string, JsonElement>` populated via `[JsonExtensionData]` on every wire-boundary record. It is the supported API for fields outside the typed surface — beta features and per-account custom fields show up here. The full unparsed payload is also available on `result.RawResponse` as a `JsonElement`.

## Errors

Every server-side error becomes a typed exception. The hierarchy lets you handle whole families with one `catch`, or pattern-match the specific code:

```
AudDException
├── AudDConnectionException        // network / TLS / timeout
├── AudDSerializationException     // malformed JSON
└── AudDApiException               // status=error from server
    ├── AudDAuthenticationException     // 900 / 901 / 903
    ├── AudDQuotaException              // 902
    ├── AudDSubscriptionException       // 904 / 905
    │   └── AudDCustomCatalogAccessException  // 904 from custom_catalog
    ├── AudDInvalidRequestException     // 50 / 51 / 600 / 601 / 602 / 700–702 / 906
    ├── AudDInvalidAudioException       // 300 / 400 / 500
    ├── AudDStreamLimitException        // 610
    ├── AudDRateLimitException          // 611
    ├── AudDNotReleasedException        // 907
    ├── AudDBlockedException            // 19 / 31337
    ├── AudDNeedsUpdateException        // 20
    └── AudDServerException             // 100 / 1000 / unknown
```

A switch on the exception subtype reads cleanly:

```csharp
try
{
    var result = await audd.RecognizeAsync("https://example.com/clip.mp3");
}
catch (AudDException exc)
{
    var action = exc switch
    {
        AudDAuthenticationException e => $"check your token: [#{e.ErrorCode}] {e.ServerMessage}",
        AudDInvalidAudioException e   => $"audio rejected: {e.ServerMessage}",
        AudDRateLimitException        => "back off and retry later",
        AudDConnectionException e     => $"transport failed: {e.Message}",
        AudDApiException e            => $"AudD #{e.ErrorCode}: {e.ServerMessage} (request_id={e.RequestId})",
        _                             => $"unexpected: {exc.Message}",
    };
    Console.Error.WriteLine(action);
}
```

Every `AudDApiException` carries `ErrorCode`, `ServerMessage`, `HttpStatus`, `RequestId`, `RequestedParams`, `RequestMethod`, `BrandedMessage`, and `RawResponse` — enough to log a full incident or open a support ticket.

## ASP.NET Core / Worker DI

Register `AudD` as a singleton in your host:

```csharp
using AudD.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAudD(opts =>
{
    builder.Configuration.GetSection("AudD").Bind(opts);
});

var app = builder.Build();
```

…with `appsettings.json`:

```json
{
  "AudD": {
    "ApiToken": "your-token",
    "MaxRetries": 3,
    "BackoffFactor": 0.5
  }
}
```

…and inject `AudD` wherever you need it:

```csharp
app.MapPost("/recognize", async (string url, AudD.AudD audd) =>
{
    var result = await audd.RecognizeAsync(url);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
```

`AddAudD` honors `IHttpClientFactory` for both the standard and enterprise endpoints — call `services.AddHttpClient(...)` or set `HttpClientName` / `EnterpriseHttpClientName` on `AudDOptions` to plug in your own handler chain (Polly, OpenTelemetry, mTLS). The enterprise client is given the long timeout it needs automatically.

Wire the inspection hook through DI to pipe events into `ILogger<AudD>` or your metrics layer:

```csharp
builder.Services.AddAudD(opts => builder.Configuration.GetSection("AudD").Bind(opts))
    .WithOnEvent((sp, evt) =>
    {
        var log = sp.GetRequiredService<ILogger<AudD.AudD>>();
        log.LogInformation("audd {Method} {Status} req={RequestId} elapsed={Elapsed}",
            evt.Method, evt.HttpStatus, evt.RequestId, evt.Elapsed);
    });
```

## Configuration

Without DI, every knob is on the `AudD` constructor:

```csharp
using var http = new HttpClient(new SocketsHttpHandler
{
    Proxy = new WebProxy("http://corp-proxy:8080"),
    UseProxy = true,
});

var audd = new AudD(
    apiToken:     "your-token",
    maxRetries:   3,                 // per-call retry budget
    backoffFactor: 0.5,              // initial backoff seconds (jittered)
    httpClient:   http,              // injected HttpClient — proxies, mTLS, shared pools
    onEvent:      evt => Console.WriteLine($"{evt.Kind} {evt.Method} {evt.HttpStatus}"));
```

`AudD` implements `IDisposable` and `IAsyncDisposable`; if you didn't pass your own `HttpClient`, dispose the client at app shutdown to release the underlying transport. A single instance is safe to share across threads and ASP.NET Core requests — construct it once, reuse it.

**Timeouts.** Defaults are 60 s read for standard endpoints and 1 hour read for the enterprise endpoint (which legitimately processes multi-hour files). Override per call with the `timeout:` parameter or by passing a `CancellationToken`.

**Retries.** Calls are classified by cost and retried accordingly:

| Class | Endpoints | Retried on |
|---|---|---|
| `Recognition` | `RecognizeAsync`, `RecognizeEnterpriseAsync`, `Advanced.*` | network errors and 5xx **before** the upload reaches the server |
| `Read` | `Streams.ListAsync`, `Streams.GetCallbackUrlAsync`, longpoll | network errors and 5xx |
| `Mutating` | `Streams.SetCallbackUrlAsync`, `Streams.AddAsync`, `Streams.DeleteAsync`, `CustomCatalog.AddAsync` | network errors and 5xx (idempotent on the server) |

Recognition calls won't double-bill your account: once the server has accepted bytes, a 5xx after that is surfaced rather than retried.

**Inspection hook.** Pass `onEvent:` (constructor) or `WithOnEvent(...)` (DI) to receive an `AudDEvent` for every request / response / exception — useful for metrics, tracing, or dropping a `RequestId` into your logs. Events never carry the api_token or request bytes; exceptions thrown from the hook are swallowed so observability can't break the request path.

## AOT and IL-trim

The library targets `net8.0` (and `net6.0` for the LTS path), declares `IsAotCompatible=true` / `IsTrimmable=true`, and parses every wire-boundary type through a source-generated `System.Text.Json` context. Publishing as a self-contained native binary works out of the box:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

No reflection-based serialization, no trim warnings.

## Streams

Real-time recognition off radio streams, broadcast feeds, and any other long-running URL. Configure once, then either receive callbacks on your server or poll for events.

```csharp
await audd.Streams.SetCallbackUrlAsync("https://your.server/audd-callback");
await audd.Streams.AddAsync("https://your.stream.url/listen.m3u8", radioId: 42);

foreach (var stream in await audd.Streams.ListAsync())
{
    Console.WriteLine($"{stream.RadioId} {stream.Url} running={stream.StreamRunning}");
}
```

The callback receives JSON; parse it into a typed event from your webhook handler:

```csharp
app.MapPost("/audd-callback", async (HttpRequest req, AudD.AudD audd) =>
{
    var ev = await audd.Streams.HandleCallbackAsync(req.Body);
    switch (ev)
    {
        case CallbackEvent.Match m:
            Console.WriteLine($"{m.Value.Song.Artist} — {m.Value.Song.Title}");
            // m.Value.Alternatives may contain extra candidates with a
            // different artist/title (variant catalog releases).
            break;
        case CallbackEvent.Notification n:
            Console.WriteLine($"notification: {n.Value.NotificationMessage}");
            break;
    }
    return Results.Ok();
});
```

`HandleCallbackAsync` reads from any `Stream`. If you already have the bytes (queue consumer, replay tool), use `AudDHelpers.ParseCallback(bodyText)` or `AudDHelpers.HandleCallback(bytes)`.

### Receiving events without a callback URL (longpoll)

If you can't expose a public callback receiver, longpoll instead. AudD still requires a callback URL to be configured for the account (`https://audd.tech/empty/` works as a no-op receiver), and the SDK preflights this for you — pass `skipCallbackCheck: true` to skip if you've already verified.

`LongpollAsync` returns a `LongpollPoll` handle with three typed `IAsyncEnumerable` streams: `Matches`, `Notifications`, and `Errors`. Iterate any one of them with `await foreach`; iterate multiple in parallel from separate tasks. Disposing the handle stops the background fetch loop.

```csharp
var category = audd.Streams.DeriveLongpollCategory(radioId: 42);

await using var poll = await audd.Streams.LongpollAsync(category, timeout: 30, cancellationToken: ct);
await foreach (var m in poll.Matches.WithCancellation(ct))
{
    Console.WriteLine($"{m.Song.Artist} — {m.Song.Title}");
}
```

Keepalive responses (`{"timeout":"no events before timeout"}`) are silently absorbed — only real recognition matches and lifecycle notifications reach `Matches` / `Notifications`.

`DeriveLongpollCategory` is a local computation: `MD5(MD5(api_token) + radio_id)[:9]`. The category alone is sufficient to subscribe — the api_token is never sent over the wire for longpoll requests.

#### Tokenless consumers

For browser widgets, embedded extensions, or any context where shipping the api_token would leak it: derive the category server-side, ship only the category to the consumer, and have the consumer use `LongpollConsumer`:

```csharp
using AudD;

// `category` was derived on your server and shared with this process.
await using var consumer = new LongpollConsumer(category: "abc123def");
await using var poll = consumer.Iterate(timeout: 30, cancellationToken: ct);
await foreach (var m in poll.Matches.WithCancellation(ct))
{
    Console.WriteLine($"{m.Song.Artist} — {m.Song.Title}");
}
```

## Custom catalog (advanced)

> **The custom-catalog endpoint is NOT how you submit audio for music recognition.**
> For recognition, use `RecognizeAsync(...)` (or `RecognizeEnterpriseAsync(...)` for files longer than 25 seconds). The custom-catalog endpoint adds songs to your *private* fingerprint database so future `RecognizeAsync` calls on your account can identify *your own* tracks.
> Requires special access — contact [api@audd.io](mailto:api@audd.io).

```csharp
await audd.CustomCatalog.AddAsync(audioId: 42, "https://my.song.mp3");
```

## License

MIT — see [LICENSE](./LICENSE).

## Support

- Documentation: <https://docs.audd.io>
- Tokens: <https://dashboard.audd.io>
- Issues: <https://github.com/AudDMusic/audd-dotnet/issues>
- Email: [api@audd.io](mailto:api@audd.io)
