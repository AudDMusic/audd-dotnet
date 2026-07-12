using System.Text.Json;
using AudD.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudD;

/// <summary>
/// Top-level client for the AudD music recognition API. Async-only public surface.
///
/// <example>
/// <code>
/// await using var audd = new AudD("test");
/// var r = await audd.RecognizeAsync("https://audd.tech/example.mp3");
/// Console.WriteLine($"{r?.Artist} - {r?.Title}");
/// </code>
/// </example>
/// </summary>
public sealed class AudD : IDisposable, IAsyncDisposable
{
    /// <summary>Standard recognition base URL.</summary>
    public const string ApiBase = "https://api.audd.io";

    /// <summary>Enterprise recognition base URL.</summary>
    public const string EnterpriseBase = "https://enterprise.audd.io";

    /// <summary>Environment variable consulted when <c>apiToken</c> is null/empty.</summary>
    public const string TokenEnvVar = "AUDD_API_TOKEN";

    /// <summary>Standard endpoint connect/read timeout (60s read).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Enterprise endpoint timeout (1 hour read).</summary>
    public static readonly TimeSpan EnterpriseTimeout = TimeSpan.FromHours(1);

    // Expected JSON kinds for known RecognitionResult / EnterpriseMatch fields.
    // Used by lenient parsing to drop a wrong-typed field rather than throw.
    private static readonly Dictionary<string, TolerantParser.Expect> RecognitionResultKnownProps = new(StringComparer.Ordinal)
    {
        ["timecode"] = TolerantParser.Expect.String,
        ["audio_id"] = TolerantParser.Expect.Number,
        ["artist"] = TolerantParser.Expect.String,
        ["title"] = TolerantParser.Expect.String,
        ["album"] = TolerantParser.Expect.String,
        ["release_date"] = TolerantParser.Expect.String,
        ["label"] = TolerantParser.Expect.String,
        ["song_link"] = TolerantParser.Expect.String,
        ["isrc"] = TolerantParser.Expect.String,
        ["upc"] = TolerantParser.Expect.String,
        ["apple_music"] = TolerantParser.Expect.Object,
        ["spotify"] = TolerantParser.Expect.Object,
        ["deezer"] = TolerantParser.Expect.Object,
        ["napster"] = TolerantParser.Expect.Object,
        ["musicbrainz"] = TolerantParser.Expect.Array,
    };

    internal static readonly Dictionary<string, TolerantParser.Expect> EnterpriseMatchKnownProps = new(StringComparer.Ordinal)
    {
        ["score"] = TolerantParser.Expect.Number,
        ["timecode"] = TolerantParser.Expect.String,
        ["artist"] = TolerantParser.Expect.String,
        ["title"] = TolerantParser.Expect.String,
        ["album"] = TolerantParser.Expect.String,
        ["release_date"] = TolerantParser.Expect.String,
        ["label"] = TolerantParser.Expect.String,
        ["isrc"] = TolerantParser.Expect.String,
        ["upc"] = TolerantParser.Expect.String,
        ["song_link"] = TolerantParser.Expect.String,
        ["start_offset"] = TolerantParser.Expect.Number,
        ["end_offset"] = TolerantParser.Expect.Number,
    };

    private string _apiToken;
    private readonly int _maxRetries;
    private readonly double _backoffFactor;
    private readonly ILogger<AudD> _logger;
    private readonly HttpTransport _http;
    private readonly HttpTransport _enterpriseHttp;
    private readonly EventEmitter _events;
    private bool _disposed;

    private Streams? _streams;
    private CustomCatalog? _customCatalog;
    private Advanced? _advanced;

    /// <summary>The API token in use. Returns the latest token after any rotations.</summary>
    public string ApiToken => System.Threading.Volatile.Read(ref _apiToken);

    /// <summary>
    /// Atomically swap the api_token used for subsequent requests. In-flight
    /// requests continue with the token they captured at send time. Throws
    /// <see cref="ArgumentException"/> on a null/empty <paramref name="newToken"/>.
    /// </summary>
    public void SetApiToken(string newToken)
    {
        if (string.IsNullOrEmpty(newToken))
        {
            throw new ArgumentException("api_token must be non-empty", nameof(newToken));
        }
        System.Threading.Interlocked.Exchange(ref _apiToken, newToken);
        _http.SetApiToken(newToken);
        _enterpriseHttp.SetApiToken(newToken);
    }

    /// <summary>Maximum retry attempts per call (default 3).</summary>
    public int MaxRetries => _maxRetries;

    /// <summary>Initial backoff seconds (jittered, exponential).</summary>
    public double BackoffFactor => _backoffFactor;

    /// <summary>Logger used for code-51 deprecation warnings and other diagnostics.</summary>
    public ILogger<AudD> Logger => _logger;

    /// <summary>
    /// Construct a client. Pass a custom <see cref="HttpClient"/> for proxies / mTLS.
    /// When <paramref name="apiToken"/> is null or empty, the SDK reads
    /// <c>AUDD_API_TOKEN</c> from the environment; if that is also unset,
    /// throws <see cref="ArgumentException"/>.
    ///
    /// <para><paramref name="onEvent"/> is an optional inspection hook (off by
    /// default) — see <see cref="AudDEvent"/>. Hook exceptions are caught and
    /// logged at debug-level via <see cref="ILogger"/> so observability never
    /// breaks a request. Hooks <b>never</b> receive the api_token or request
    /// body bytes.</para>
    /// </summary>
    public AudD(
        string? apiToken,
        int maxRetries = 3,
        double backoffFactor = 0.5,
        HttpClient? httpClient = null,
        HttpClient? enterpriseHttpClient = null,
        ILogger<AudD>? logger = null,
        Action<AudDEvent>? onEvent = null)
    {
        var resolved = ResolveToken(apiToken);
        _apiToken = resolved;
        _maxRetries = maxRetries < 1 ? 1 : maxRetries;
        _backoffFactor = backoffFactor;
        _logger = logger ?? NullLogger<AudD>.Instance;
        _http = new HttpTransport(resolved, httpClient, DefaultTimeout);
        _enterpriseHttp = new HttpTransport(resolved, enterpriseHttpClient, EnterpriseTimeout);
        _events = new EventEmitter(onEvent, _logger);
    }

    /// <summary>
    /// DI-oriented constructor that resolves a fresh <see cref="HttpClient"/> per
    /// request from the supplied resolvers (backed by <c>IHttpClientFactory</c>),
    /// so handler rotation is honored. The SDK's standard/enterprise default
    /// deadlines are enforced independently of each resolved client's own timeout.
    /// </summary>
    internal AudD(
        string? apiToken,
        Func<HttpClient> httpClientResolver,
        Func<HttpClient> enterpriseHttpClientResolver,
        int maxRetries = 3,
        double backoffFactor = 0.5,
        ILogger<AudD>? logger = null,
        Action<AudDEvent>? onEvent = null)
    {
        if (httpClientResolver is null) throw new ArgumentNullException(nameof(httpClientResolver));
        if (enterpriseHttpClientResolver is null) throw new ArgumentNullException(nameof(enterpriseHttpClientResolver));
        var resolved = ResolveToken(apiToken);
        _apiToken = resolved;
        _maxRetries = maxRetries < 1 ? 1 : maxRetries;
        _backoffFactor = backoffFactor;
        _logger = logger ?? NullLogger<AudD>.Instance;
        _http = new HttpTransport(resolved, httpClientResolver, DefaultTimeout);
        _enterpriseHttp = new HttpTransport(resolved, enterpriseHttpClientResolver, EnterpriseTimeout);
        _events = new EventEmitter(onEvent, _logger);
    }

    /// <summary>
    /// Build a client using the api_token from the <c>AUDD_API_TOKEN</c> environment
    /// variable. Throws <see cref="ArgumentException"/> when the env var is unset
    /// or empty.
    /// </summary>
    public static AudD FromEnvironment(
        int maxRetries = 3,
        double backoffFactor = 0.5,
        HttpClient? httpClient = null,
        HttpClient? enterpriseHttpClient = null,
        ILogger<AudD>? logger = null,
        Action<AudDEvent>? onEvent = null)
        => new AudD(
            apiToken: null,
            maxRetries: maxRetries,
            backoffFactor: backoffFactor,
            httpClient: httpClient,
            enterpriseHttpClient: enterpriseHttpClient,
            logger: logger,
            onEvent: onEvent);

    private static string ResolveToken(string? apiToken)
    {
        if (!string.IsNullOrEmpty(apiToken)) return apiToken!;
        var fromEnv = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv!;
        throw new ArgumentException(
            $"AudD apiToken not supplied and {TokenEnvVar} env var is unset. " +
            "Get a token at https://dashboard.audd.io and pass it as " +
            $"new AudD(apiToken: ...) or set {TokenEnvVar}.",
            nameof(apiToken));
    }

    /// <summary>Streams namespace — set callback URL, manage streams, longpoll.</summary>
    public Streams Streams => _streams ??= new Streams(
        _http,
        () => System.Threading.Volatile.Read(ref _apiToken),
        _maxRetries, _backoffFactor, _logger, _events);

    /// <summary>Custom-catalog namespace. NOT for music recognition — see <see cref="AudD.CustomCatalog"/>.</summary>
    public CustomCatalog CustomCatalog => _customCatalog ??= new CustomCatalog(_http, _maxRetries, _backoffFactor, _logger, _events);

    /// <summary>Advanced namespace — lyrics search + raw escape hatch. Deliberately not on the main surface.</summary>
    public Advanced Advanced => _advanced ??= new Advanced(_http, _maxRetries, _backoffFactor, _logger, _events);

    /// <summary>
    /// Recognize a short audio clip. Source may be a URL string, a file path, a
    /// <see cref="FileInfo"/>, a <see cref="System.IO.Stream"/>, or a byte buffer.
    /// Returns <c>null</c> when the server returns success with <c>result=null</c>.
    /// </summary>
    public async Task<RecognitionResult?> RecognizeAsync(
        string sourceUrlOrPath,
        IEnumerable<string>? returnMetadata = null,
        string? market = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await RecognizeAsync(RecognitionSource.From(sourceUrlOrPath), returnMetadata, market, extraParameters, timeout, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc cref="RecognizeAsync(string, IEnumerable{string}?, string?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<RecognitionResult?> RecognizeAsync(
        FileInfo file,
        IEnumerable<string>? returnMetadata = null,
        string? market = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeAsync(RecognitionSource.From(file), returnMetadata, market, extraParameters, timeout, cancellationToken);

    /// <inheritdoc cref="RecognizeAsync(string, IEnumerable{string}?, string?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<RecognitionResult?> RecognizeAsync(
        byte[] bytes,
        IEnumerable<string>? returnMetadata = null,
        string? market = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeAsync(RecognitionSource.From(bytes), returnMetadata, market, extraParameters, timeout, cancellationToken);

    /// <inheritdoc cref="RecognizeAsync(string, IEnumerable{string}?, string?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<RecognitionResult?> RecognizeAsync(
        System.IO.Stream stream,
        IEnumerable<string>? returnMetadata = null,
        string? market = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeAsync(RecognitionSource.From(stream), returnMetadata, market, extraParameters, timeout, cancellationToken);

    private async Task<RecognitionResult?> RecognizeAsync(
        RecognitionSource source,
        IEnumerable<string>? returnMetadata,
        string? market,
        IDictionary<string, string>? extraParameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>();
        if (extraParameters is not null)
        {
            foreach (var kv in extraParameters) fields[kv.Key] = kv.Value;
        }
        if (returnMetadata is not null)
        {
            var ret = string.Join(",", returnMetadata);
            if (!string.IsNullOrEmpty(ret)) fields["return"] = ret;
        }
        if (market is not null) fields["market"] = market;

        var body = await PostRecognitionAsync(_http, $"{ApiBase}/", "recognize", source, fields, timeout, cancellationToken).ConfigureAwait(false);
        // A missing, null, or wrong-typed `result` reads as "no match" — response
        // parsing never throws on missing or wrong-typed fields.
        if (!body.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Never throw on a wrong-typed field in a successful result — degrade
        // per-field (a bad field reads as null/0/absent).
        var parsed = TolerantParser.ParseObject(
            result,
            AudDJsonContext.Default.RecognitionResult,
            RecognitionResultKnownProps,
            static () => new RecognitionResult());
        return parsed with { RawResponse = body.Clone() };
    }

    /// <summary>
    /// Recognize against the enterprise endpoint (long files). Returns an empty
    /// list when no matches are found. The SDK streams the upload — multi-GB files
    /// are not buffered fully in memory.
    /// </summary>
    public Task<IReadOnlyList<EnterpriseMatch>> RecognizeEnterpriseAsync(
        string sourceUrlOrPath,
        IEnumerable<string>? returnMetadata = null,
        int? skip = null,
        int? every = null,
        int? limit = null,
        int? skipFirstSeconds = null,
        bool? useTimecode = null,
        bool? accurateOffsets = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeEnterpriseAsync(RecognitionSource.From(sourceUrlOrPath),
            returnMetadata, skip, every, limit, skipFirstSeconds, useTimecode, accurateOffsets,
            extraParameters, timeout, cancellationToken);

    /// <inheritdoc cref="RecognizeEnterpriseAsync(string, IEnumerable{string}?, int?, int?, int?, int?, bool?, bool?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<IReadOnlyList<EnterpriseMatch>> RecognizeEnterpriseAsync(
        FileInfo file,
        IEnumerable<string>? returnMetadata = null,
        int? skip = null,
        int? every = null,
        int? limit = null,
        int? skipFirstSeconds = null,
        bool? useTimecode = null,
        bool? accurateOffsets = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeEnterpriseAsync(RecognitionSource.From(file),
            returnMetadata, skip, every, limit, skipFirstSeconds, useTimecode, accurateOffsets,
            extraParameters, timeout, cancellationToken);

    /// <inheritdoc cref="RecognizeEnterpriseAsync(string, IEnumerable{string}?, int?, int?, int?, int?, bool?, bool?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<IReadOnlyList<EnterpriseMatch>> RecognizeEnterpriseAsync(
        byte[] bytes,
        IEnumerable<string>? returnMetadata = null,
        int? skip = null,
        int? every = null,
        int? limit = null,
        int? skipFirstSeconds = null,
        bool? useTimecode = null,
        bool? accurateOffsets = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeEnterpriseAsync(RecognitionSource.From(bytes),
            returnMetadata, skip, every, limit, skipFirstSeconds, useTimecode, accurateOffsets,
            extraParameters, timeout, cancellationToken);

    /// <inheritdoc cref="RecognizeEnterpriseAsync(string, IEnumerable{string}?, int?, int?, int?, int?, bool?, bool?, IDictionary{string,string}?, TimeSpan?, CancellationToken)"/>
    public Task<IReadOnlyList<EnterpriseMatch>> RecognizeEnterpriseAsync(
        System.IO.Stream stream,
        IEnumerable<string>? returnMetadata = null,
        int? skip = null,
        int? every = null,
        int? limit = null,
        int? skipFirstSeconds = null,
        bool? useTimecode = null,
        bool? accurateOffsets = null,
        IDictionary<string, string>? extraParameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RecognizeEnterpriseAsync(RecognitionSource.From(stream),
            returnMetadata, skip, every, limit, skipFirstSeconds, useTimecode, accurateOffsets,
            extraParameters, timeout, cancellationToken);

    private async Task<IReadOnlyList<EnterpriseMatch>> RecognizeEnterpriseAsync(
        RecognitionSource source,
        IEnumerable<string>? returnMetadata,
        int? skip,
        int? every,
        int? limit,
        int? skipFirstSeconds,
        bool? useTimecode,
        bool? accurateOffsets,
        IDictionary<string, string>? extraParameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>();
        if (extraParameters is not null)
        {
            foreach (var kv in extraParameters) fields[kv.Key] = kv.Value;
        }
        if (returnMetadata is not null)
        {
            var ret = string.Join(",", returnMetadata);
            if (!string.IsNullOrEmpty(ret)) fields["return"] = ret;
        }
        if (skip.HasValue) fields["skip"] = skip.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (every.HasValue) fields["every"] = every.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (limit.HasValue) fields["limit"] = limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (skipFirstSeconds.HasValue) fields["skip_first_seconds"] = skipFirstSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (useTimecode.HasValue) fields["use_timecode"] = useTimecode.Value ? "true" : "false";
        // Default accurate offsets on so StartSeconds/EndSeconds are precise;
        // a caller passing false opts out.
        fields["accurate_offsets"] = accurateOffsets.GetValueOrDefault(true) ? "true" : "false";

        var body = await PostRecognitionAsync(_enterpriseHttp, $"{EnterpriseBase}/", "recognize_enterprise", source, fields, timeout, cancellationToken).ConfigureAwait(false);
        var matches = new List<EnterpriseMatch>();
        if (body.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
        {
            foreach (var chunkEl in result.EnumerateArray())
            {
                if (chunkEl.ValueKind != JsonValueKind.Object) continue;

                // Read the chunk offset defensively — a wrong-typed offset must not
                // discard the chunk's songs, only leave StartSeconds/EndSeconds unset.
                string? offset = null;
                if (chunkEl.TryGetProperty("offset", out var offsetEl) && offsetEl.ValueKind == JsonValueKind.String)
                {
                    offset = offsetEl.GetString();
                }
                var chunkBase = AudDHelpers.OffsetToSeconds(offset);

                if (!chunkEl.TryGetProperty("songs", out var songsEl) || songsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var songEl in songsEl.EnumerateArray())
                {
                    if (songEl.ValueKind != JsonValueKind.Object) continue;

                    // Parse each song leniently — one bad field degrades that field,
                    // never the song, and never the whole chunk.
                    var s = TolerantParser.ParseObject(
                        songEl,
                        AudDJsonContext.Default.EnterpriseMatch,
                        EnterpriseMatchKnownProps,
                        static () => new EnterpriseMatch());
                    var match = s with { RawResponse = songEl.Clone() };
                    if (chunkBase is double baseSeconds)
                    {
                        match = match with
                        {
                            StartSeconds = baseSeconds + (s.StartOffset ?? 0) / 1000.0,
                            EndSeconds = baseSeconds + (s.EndOffset ?? 0) / 1000.0,
                        };
                    }
                    matches.Add(match);
                }
            }
        }
        return matches;
    }

    private async Task<JsonElement> PostRecognitionAsync(
        HttpTransport transport,
        string url,
        string method,
        RecognitionSource source,
        IDictionary<string, string> fields,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var policy = new RetryPolicy(RetryClass.Recognition, _maxRetries, _backoffFactor);
        var startedAt = DateTime.UtcNow;
        _events.EmitRequest(method, url);
        HttpResponseEnvelope? lastResp = null;
        try
        {
            var resp = await Retry.RunAsync(
                async ct =>
                {
                    return await transport.PostFormAsync(
                        url,
                        extraFields => source.BuildContent(extraFields),
                        fields,
                        ct,
                        // A caller-supplied timeout overrides the transport
                        // default and can both shorten and extend the deadline.
                        requestTimeout: timeout).ConfigureAwait(false);
                },
                policy,
                cancellationToken).ConfigureAwait(false);
            lastResp = resp;
            var body = ResponseDecoder.DecodeOrThrow(resp, _logger);
            _events.EmitResponse(method, url, resp.RequestId, resp.HttpStatus, DateTime.UtcNow - startedAt);
            return body;
        }
        catch (HttpRequestException exc)
        {
            _events.EmitException(method, url, exc, DateTime.UtcNow - startedAt);
            throw new AudDConnectionException(exc.Message, exc);
        }
        catch (TaskCanceledException exc) when (!cancellationToken.IsCancellationRequested)
        {
            _events.EmitException(method, url, exc, DateTime.UtcNow - startedAt);
            throw new AudDConnectionException(exc.Message, exc);
        }
        catch (AudDApiException exc)
        {
            _events.EmitException(method, url, exc, DateTime.UtcNow - startedAt,
                httpStatus: exc.HttpStatus, requestId: exc.RequestId, errorCode: exc.ErrorCode);
            throw;
        }
        catch (Exception exc)
        {
            _events.EmitException(method, url, exc, DateTime.UtcNow - startedAt,
                httpStatus: lastResp?.HttpStatus, requestId: lastResp?.RequestId);
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _enterpriseHttp.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
