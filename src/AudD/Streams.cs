using System.Text.Json;
using AudD.Internal;
using Microsoft.Extensions.Logging;

namespace AudD;

/// <summary>
/// Streams namespace — set callback URL, manage real-time stream recognition,
/// and longpoll for events. See spec §4.1.
/// </summary>
public sealed class Streams
{
    private const int NoCallbackErrorCode = 19;
    private const string PreflightHint =
        "Longpoll won't deliver events because no callback URL is configured for this account. " +
        "Set one first via Streams.SetCallbackUrlAsync(...) — `https://audd.tech/empty/` is fine if " +
        "you only want longpolling and don't need a real receiver. " +
        "To skip this check, pass skipCallbackCheck=true.";

    private readonly HttpTransport _http;
    private readonly Func<string> _tokenProvider;
    private readonly int _maxRetries;
    private readonly double _backoffFactor;
    private readonly ILogger _logger;
    private readonly EventEmitter _events;

    internal Streams(HttpTransport http, Func<string> tokenProvider, int maxRetries, double backoffFactor, ILogger logger, EventEmitter? events = null)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _maxRetries = maxRetries;
        _backoffFactor = backoffFactor;
        _logger = logger;
        _events = events ?? EventEmitter.Disabled;
    }

    private RetryPolicy ReadPolicy() => new(RetryClass.Read, _maxRetries, _backoffFactor);
    private RetryPolicy MutatingPolicy() => new(RetryClass.Mutating, _maxRetries, _backoffFactor);

    /// <summary>Set the callback URL the AudD server posts to on stream events.</summary>
    /// <param name="url">Your callback URL.</param>
    /// <param name="returnMetadata">
    /// Optional metadata list (e.g. <c>"apple_music,spotify"</c>). When present, the SDK
    /// appends <c>?return=&lt;metadata&gt;</c> to the URL. If the URL already has a <c>return</c>
    /// query parameter, the SDK throws rather than silently overwriting.
    /// </param>
    /// <param name="extraParameters">Additional form fields the typed params don't cover. Typed params (<c>url</c>) win on collision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetCallbackUrlAsync(
        string url,
        string? returnMetadata = null,
        IDictionary<string, string>? extraParameters = null,
        CancellationToken cancellationToken = default)
        => SetCallbackUrlInternalAsync(AudDHelpers.AddReturnToUrl(url, returnMetadata), extraParameters, cancellationToken);

    /// <summary>Set the callback URL with a list of return-metadata fields.</summary>
    /// <param name="url">Your callback URL.</param>
    /// <param name="returnMetadata">List of return-metadata fields (e.g. <c>["apple_music","spotify"]</c>).</param>
    /// <param name="extraParameters">Additional form fields the typed params don't cover. Typed params (<c>url</c>) win on collision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetCallbackUrlAsync(
        string url,
        IEnumerable<string>? returnMetadata,
        IDictionary<string, string>? extraParameters = null,
        CancellationToken cancellationToken = default)
        => SetCallbackUrlInternalAsync(AudDHelpers.AddReturnToUrl(url, returnMetadata), extraParameters, cancellationToken);

    private async Task SetCallbackUrlInternalAsync(string url, IDictionary<string, string>? extraParameters, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>();
        if (extraParameters is not null)
        {
            foreach (var kv in extraParameters) data[kv.Key] = kv.Value;
        }
        data["url"] = url;
        await PostAsync("setCallbackUrl", data, MutatingPolicy(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get the current callback URL configured on the account.</summary>
    public async Task<string> GetCallbackUrlAsync(CancellationToken cancellationToken = default)
    {
        var body = await PostAsync("getCallbackUrl", null, ReadPolicy(), cancellationToken).ConfigureAwait(false);
        if (body.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
        {
            return r.GetString() ?? "";
        }
        return "";
    }

    /// <summary>
    /// Add a stream. <paramref name="url"/> accepts direct stream URLs (DASH/Icecast/HLS/m3u/m3u8)
    /// and shortcuts: <c>twitch:&lt;channel&gt;</c>, <c>youtube:&lt;video_id&gt;</c>,
    /// <c>youtube-ch:&lt;channel_id&gt;</c>. <paramref name="callbacks"/>=<c>"before"</c>
    /// delivers callbacks at song start instead of song end.
    /// </summary>
    /// <param name="url">Stream URL.</param>
    /// <param name="radioId">Caller-side identifier for this stream.</param>
    /// <param name="callbacks">Pass <c>"before"</c> for song-start callbacks.</param>
    /// <param name="extraParameters">
    /// Additional form fields the typed params don't cover. Typed params
    /// (<c>url</c>, <c>radio_id</c>, <c>callbacks</c>) win on collision.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddAsync(
        string url,
        long radioId,
        string? callbacks = null,
        IDictionary<string, string>? extraParameters = null,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string>();
        if (extraParameters is not null)
        {
            foreach (var kv in extraParameters) data[kv.Key] = kv.Value;
        }
        data["url"] = url;
        data["radio_id"] = radioId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (callbacks is not null) data["callbacks"] = callbacks;
        await PostAsync("addStream", data, MutatingPolicy(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Update the URL for an existing stream.</summary>
    public async Task SetUrlAsync(long radioId, string url, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string>
        {
            ["radio_id"] = radioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["url"] = url,
        };
        await PostAsync("setStreamUrl", data, MutatingPolicy(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete a stream.</summary>
    public async Task DeleteAsync(long radioId, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string>
        {
            ["radio_id"] = radioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await PostAsync("deleteStream", data, MutatingPolicy(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>List all streams for the account.</summary>
    public async Task<IReadOnlyList<Stream>> ListAsync(CancellationToken cancellationToken = default)
    {
        var body = await PostAsync("getStreams", null, ReadPolicy(), cancellationToken).ConfigureAwait(false);
        if (body.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            var output = new List<Stream>();
            foreach (var e in r.EnumerateArray())
            {
                var s = e.Deserialize(AudDJsonContext.Default.Stream);
                if (s is not null) output.Add(s);
            }
            return output;
        }
        return Array.Empty<Stream>();
    }

    /// <summary>
    /// Start a long-poll subscription. Returns a <see cref="LongpollPoll"/> handle
    /// whose <see cref="LongpollPoll.Matches"/> / <see cref="LongpollPoll.Notifications"/> /
    /// <see cref="LongpollPoll.Errors"/> streams are filled by a background fetch loop.
    /// Dispose the handle to stop polling.
    ///
    /// <para>Keepalive responses (<c>{"timeout":"no events before timeout"}</c>) are
    /// silently absorbed — only real recognition matches and lifecycle notifications
    /// reach the consumer.</para>
    ///
    /// <para>On entry, preflights <see cref="GetCallbackUrlAsync"/> unless
    /// <paramref name="skipCallbackCheck"/> is true — the AudD server requires a
    /// callback URL to be configured before longpoll will deliver events.</para>
    /// </summary>
    public async Task<LongpollPoll> LongpollAsync(
        string category,
        long? sinceTime = null,
        int timeout = 50,
        bool skipCallbackCheck = false,
        CancellationToken cancellationToken = default)
    {
        if (!skipCallbackCheck)
        {
            try
            {
                _ = await GetCallbackUrlAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AudDApiException exc) when (exc.ErrorCode == NoCallbackErrorCode)
            {
                throw new AudDInvalidRequestException(
                    errorCode: 0,
                    serverMessage: PreflightHint,
                    httpStatus: exc.HttpStatus,
                    requestId: exc.RequestId);
            }
        }

        var longpollUrl = $"{AudD.ApiBase}/longpoll/";
        var capturedTimeout = timeout;
        var capturedCategory = category;

        var fetcher = new LongpollFetcher(
            fetchAsync: async (since, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var qs = new Dictionary<string, string>
                {
                    ["category"] = capturedCategory,
                    ["timeout"] = capturedTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (since.HasValue) qs["since_time"] = since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

                HttpResponseEnvelope envelope;
                var startedAt = DateTime.UtcNow;
                _events.EmitRequest("longpoll", longpollUrl);
                try
                {
                    envelope = await Retry.RunAsync(
                        c => _http.GetAsync(longpollUrl, qs, c),
                        ReadPolicy(),
                        ct).ConfigureAwait(false);
                }
                catch (HttpRequestException exc)
                {
                    _events.EmitException("longpoll", longpollUrl, exc, DateTime.UtcNow - startedAt);
                    throw new AudDConnectionException(exc.Message, exc);
                }
                catch (Exception exc) when (exc is not OperationCanceledException)
                {
                    _events.EmitException("longpoll", longpollUrl, exc, DateTime.UtcNow - startedAt);
                    throw;
                }

                if (envelope.JsonBody is null || envelope.JsonBody.Value.ValueKind != JsonValueKind.Object)
                {
                    var serExc = new AudDSerializationException("Unparseable longpoll response", envelope.RawText);
                    _events.EmitException("longpoll", longpollUrl, serExc, DateTime.UtcNow - startedAt,
                        httpStatus: envelope.HttpStatus, requestId: envelope.RequestId);
                    throw serExc;
                }
                _events.EmitResponse("longpoll", longpollUrl, envelope.RequestId, envelope.HttpStatus, DateTime.UtcNow - startedAt);
                return envelope.JsonBody.Value;
            },
            initialSinceTime: sinceTime);

        return LongpollPoll.Start(fetcher, cancellationToken);
    }

    /// <summary>
    /// Start a long-poll subscription keyed on a stream's <paramref name="radioId"/>.
    /// Convenience overload: derives the 9-char category locally via
    /// <see cref="DeriveLongpollCategory(long)"/> and delegates to
    /// <see cref="LongpollAsync(string, long?, int, bool, CancellationToken)"/>.
    ///
    /// <para>Use the <see cref="LongpollAsync(string, long?, int, bool, CancellationToken)"/>
    /// overload when you only have a pre-derived category string (e.g. tokenless
    /// browser/mobile/embedded clients where the server derived the category and
    /// shipped just the string).</para>
    /// </summary>
    public Task<LongpollPoll> LongpollAsync(
        long radioId,
        long? sinceTime = null,
        int timeout = 50,
        bool skipCallbackCheck = false,
        CancellationToken cancellationToken = default)
        => LongpollAsync(DeriveLongpollCategory(radioId), sinceTime, timeout, skipCallbackCheck, cancellationToken);

    /// <summary>Compute the 9-char longpoll category locally from token + radio_id.</summary>
    public string DeriveLongpollCategory(long radioId) => AudDHelpers.DeriveLongpollCategory(_tokenProvider(), radioId);

    /// <summary>Parse a streams callback POST body (the JSON your webhook receives).</summary>
    public CallbackEvent ParseCallback(JsonElement body) => AudDHelpers.ParseCallback(body);

    /// <inheritdoc cref="ParseCallback(JsonElement)"/>
    public CallbackEvent ParseCallback(string body) => AudDHelpers.ParseCallback(body);

    /// <summary>
    /// Read and parse a streams callback POST body from <paramref name="bodyStream"/>.
    /// Use from your webhook handler — pass <c>HttpRequest.Body</c> (ASP.NET Core),
    /// <c>req.InputStream</c> (HttpListener), or any <see cref="System.IO.Stream"/>.
    /// Does not close the stream.
    /// </summary>
    public Task<CallbackEvent> HandleCallbackAsync(System.IO.Stream bodyStream, CancellationToken cancellationToken = default)
        => AudDHelpers.HandleCallbackAsync(bodyStream, cancellationToken);

    private async Task<JsonElement> PostAsync(
        string method,
        IDictionary<string, string>? data,
        RetryPolicy policy,
        CancellationToken cancellationToken)
    {
        var url = $"{AudD.ApiBase}/{method}/";
        var startedAt = DateTime.UtcNow;
        _events.EmitRequest(method, url);
        try
        {
            var resp = await Retry.RunAsync(
                ct => _http.PostFormAsync(
                    url,
                    extra =>
                    {
                        var form = new MultipartFormDataContent();
                        foreach (var kv in extra) form.Add(new StringContent(kv.Value), kv.Key);
                        return form;
                    },
                    data,
                    ct),
                policy,
                cancellationToken).ConfigureAwait(false);
            var body = ResponseDecoder.DecodeOrThrow(resp, _logger);
            _events.EmitResponse(method, url, resp.RequestId, resp.HttpStatus, DateTime.UtcNow - startedAt);
            return body;
        }
        catch (HttpRequestException exc)
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
            _events.EmitException(method, url, exc, DateTime.UtcNow - startedAt);
            throw;
        }
    }
}
