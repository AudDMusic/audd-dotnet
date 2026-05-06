# AudD

Official .NET SDK for the [AudD](https://audd.io) music recognition API.
.NET 8+, async-first, AOT/trim-clean (System.Text.Json source generators), DI-friendly.

## Quickstart

```bash
dotnet add package AudD
```

```csharp
using AudD;

var audd = new AudD("test");  // your token from https://dashboard.audd.io
var result = await audd.RecognizeAsync("https://audd.tech/example.mp3");
if (result is not null)
{
    Console.WriteLine($"{result.Artist} — {result.Title}");
}
```

## ASP.NET Core / Worker DI

```csharp
builder.Services.AddAudD(opts =>
{
    opts.ApiToken = builder.Configuration["AudD:ApiToken"];
    opts.MaxRetries = 3;
});
```

`AddAudD` registers `AudD` as a singleton, plays nice with `IConfiguration.Bind`, and pulls `HttpClient` from `IHttpClientFactory` when present. Chain `.WithOnEvent(...)` to wire the inspection hook to `ILogger<AudD>`.

## Capabilities

- `audd.RecognizeAsync(...)` — public-database recognition.
- `audd.RecognizeEnterpriseAsync(...)` — long-form (up to ~120 min) audio with chunked timecodes.
- `audd.Streams.*` — radio-station stream setup and callback handling.
- `audd.CustomCatalog.*` — custom-fingerprint upload + recognition.
- `audd.Advanced.*` — typed wrappers for advanced endpoints.

For longpoll-based delivery (token-bound or tokenless), see `audd.Streams.LongpollAsync(...)` and `LongpollConsumer`.

## AOT / IL-trim

The library ships `IsAotCompatible=true` and uses `System.Text.Json` source generators (`AudDJsonContext`) so deserialization stays trim-safe. To verify in your own app:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## Errors

`AudDException` and its subclasses (`AudDServerException`, `AudDConnectionException`, `AudDSerializationException`) carry the HTTP status, request ID, and raw response body for diagnostic plumbing. See [src/AudD/Errors](src/AudD/Errors) for the full hierarchy.

## License

MIT — see [LICENSE](LICENSE).

## Support

- API reference: [docs.audd.io](https://docs.audd.io)
- Issues: [github.com/AudDMusic/audd-dotnet/issues](https://github.com/AudDMusic/audd-dotnet/issues)
- Email: [api@audd.io](mailto:api@audd.io)
