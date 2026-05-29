using System.Text.Json;
using AudD.Internal;
using Microsoft.Extensions.Logging;

namespace AudD;

/// <summary>
/// Advanced namespace — lyrics + raw escape hatch. Reach via <c>audd.Advanced.*</c>.
/// Deliberately not on the main client surface. Uses RECOGNITION retry policy.
/// </summary>
public sealed class Advanced
{
    private readonly HttpTransport _http;
    private readonly int _maxRetries;
    private readonly double _backoffFactor;
    private readonly ILogger _logger;
    private readonly EventEmitter _events;

    internal Advanced(HttpTransport http, int maxRetries, double backoffFactor, ILogger logger, EventEmitter? events = null)
    {
        _http = http;
        _maxRetries = maxRetries;
        _backoffFactor = backoffFactor;
        _logger = logger;
        _events = events ?? EventEmitter.Disabled;
    }

    /// <summary>Search lyrics. Returns matching <see cref="LyricsResult"/> entries.</summary>
    public async Task<IReadOnlyList<LyricsResult>> FindLyricsAsync(string query, CancellationToken cancellationToken = default)
    {
        var body = await RawRequestAsync("findLyrics", new Dictionary<string, string> { ["q"] = query }, cancellationToken).ConfigureAwait(false);
        // raw_request returns the full body untouched; surface error here.
        if (body.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "error")
        {
            throw ErrorRaiser.BuildFromErrorBody(body, httpStatus: 200, requestId: null);
        }
        var output = new List<LyricsResult>();
        if (body.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in r.EnumerateArray())
            {
                try
                {
                    var lyr = e.Deserialize(AudDJsonContext.Default.LyricsResult);
                    if (lyr is not null) output.Add(lyr);
                }
                catch (JsonException)
                {
                    // Skip a malformed lyrics entry rather than failing the search.
                }
            }
        }
        return output;
    }

    /// <summary>
    /// Hit any AudD endpoint by method name and return the raw JSON. Useful for
    /// endpoints not yet wrapped by typed methods on this SDK.
    /// </summary>
    public async Task<JsonElement> RawRequestAsync(
        string method,
        IDictionary<string, string>? @params = null,
        CancellationToken cancellationToken = default)
    {
        var policy = new RetryPolicy(RetryClass.Recognition, _maxRetries, _backoffFactor);
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
                    @params,
                    ct),
                policy,
                cancellationToken).ConfigureAwait(false);
            // For raw_request we deliberately don't treat status=error as a throw at this layer;
            // we return the whole body so callers can inspect. (FindLyrics raises above on error.)
            if (!resp.JsonBody.HasValue || resp.JsonBody.Value.ValueKind != JsonValueKind.Object)
            {
                var ex = new AudDSerializationException("Unparseable response", resp.RawText);
                _events.EmitException(method, url, ex, DateTime.UtcNow - startedAt,
                    httpStatus: resp.HttpStatus, requestId: resp.RequestId);
                throw ex;
            }
            _events.EmitResponse(method, url, resp.RequestId, resp.HttpStatus, DateTime.UtcNow - startedAt);
            return resp.JsonBody.Value;
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
