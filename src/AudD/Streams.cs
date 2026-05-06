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
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetCallbackUrlAsync(
        string url,
        string? returnMetadata = null,
        CancellationToken cancellationToken = default)
        => SetCallbackUrlInternalAsync(AudDHelpers.AddReturnToUrl(url, returnMetadata), cancellationToken);

    /// <summary>Set the callback URL with a list of return-metadata fields.</summary>
    /// <param name="url">Your callback URL.</param>
    /// <param name="returnMetadata">List of return-metadata fields (e.g. <c>["apple_music","spotify"]</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SetCallbackUrlAsync(
        string url,
        IEnumerable<string>? returnMetadata,
        CancellationToken cancellationToken = default)
        => SetCallbackUrlInternalAsync(AudDHelpers.AddReturnToUrl(url, returnMetadata), cancellationToken);

    private async Task SetCallbackUrlInternalAsync(string url, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string> { ["url"] = url };
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
    public async Task AddAsync(
        string url,
        long radioId,
        string? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string>
        {
            ["url"] = url,
            ["radio_id"] = radioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
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
    /// Yield successive longpoll responses (timeout or event variants).
    /// On entry, the SDK preflights <see cref="GetCallbackUrlAsync"/> unless
    /// <paramref name="skipCallbackCheck"/> is true.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> LongpollAsync(
        string category,
        long? sinceTime = null,
        int timeout = 50,
        bool skipCallbackCheck = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        var since = sinceTime;
        var longpollUrl = $"{AudD.ApiBase}/longpoll/";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var qs = new Dictionary<string, string>
            {
                ["category"] = category,
                ["timeout"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (since.HasValue) qs["since_time"] = since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            HttpResponseEnvelope envelope;
            var startedAt = DateTime.UtcNow;
            _events.EmitRequest("longpoll", longpollUrl);
            try
            {
                envelope = await Retry.RunAsync(
                    ct => _http.GetAsync(longpollUrl, qs, ct),
                    ReadPolicy(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exc)
            {
                _events.EmitException("longpoll", longpollUrl, exc, DateTime.UtcNow - startedAt);
                throw new AudDConnectionException(exc.Message, exc);
            }
            catch (Exception exc)
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
            var body = envelope.JsonBody.Value;
            yield return body;
            if (body.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
            {
                since = ts.GetInt64();
            }
        }
    }

    /// <summary>Compute the 9-char longpoll category locally from token + radio_id.</summary>
    public string DeriveLongpollCategory(long radioId) => AudDHelpers.DeriveLongpollCategory(_tokenProvider(), radioId);

    /// <summary>Parse a streams callback POST body (the JSON your webhook receives).</summary>
    public StreamCallbackPayload ParseCallback(JsonElement body) => StreamCallbackPayload.Parse(body);

    /// <inheritdoc cref="ParseCallback(JsonElement)"/>
    public StreamCallbackPayload ParseCallback(string body) => AudDHelpers.ParseCallback(body);

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
