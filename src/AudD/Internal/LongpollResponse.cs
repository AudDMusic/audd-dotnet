using System.Text.Json;

namespace AudD.Internal;

/// <summary>What one longpoll response carried, ready for delivery.</summary>
internal sealed class LongpollBatch
{
    /// <summary>A response with no events and no timestamp, in none of the documented shapes.</summary>
    public static readonly LongpollBatch Unrecognized = new(Array.Empty<CallbackEvent>(), timestamp: null, recognized: false);

    public LongpollBatch(IReadOnlyList<CallbackEvent> events, long? timestamp, bool recognized)
    {
        Events = events;
        Timestamp = timestamp;
        Recognized = recognized;
    }

    /// <summary>Events to deliver, in the order the server sent them.</summary>
    public IReadOnlyList<CallbackEvent> Events { get; }

    /// <summary>
    /// The <c>since_time</c> for the next request: the largest event
    /// <c>timestamp</c>, or the timeout response's <c>timestamp</c>. Null when
    /// the response carried none.
    /// </summary>
    public long? Timestamp { get; }

    /// <summary>False for a body in none of the documented shapes (non-JSON, empty, unknown keys).</summary>
    public bool Recognized { get; }
}

/// <summary>
/// Reads <c>GET /longpoll/</c> responses. The server answers HTTP 200 with one of:
/// <list type="bullet">
///   <item><description><c>{"events":[{"timestamp":…,"category":…,"data":{…}}, …]}</c>: each
///     <c>data</c> is the body a callback URL receives.</description></item>
///   <item><description><c>{"timeout":"no events before timeout","timestamp":…}</c>.</description></item>
///   <item><description><c>{"error":"…"}</c> for an invalid request; the same request gets the same error.</description></item>
/// </list>
/// A body with <c>result</c> or <c>notification</c> at the top level (and an optional
/// top-level <c>timestamp</c>) is read as a single event.
/// </summary>
internal static class LongpollResponse
{
    private const int HttpClientErrorFloor = 400;

    /// <summary>
    /// Read one longpoll response. Throws for a response that ends the subscription:
    /// the server's error body (<c>{"error":"…"}</c> gives <see cref="AudDInvalidRequestException"/>
    /// carrying the server's text; a <c>status=error</c> body gives the mapped
    /// <see cref="AudDApiException"/>) and an HTTP 4xx/5xx status
    /// (<see cref="AudDServerException"/>). Every other response comes back as a
    /// batch, possibly empty.
    /// </summary>
    public static LongpollBatch Read(HttpResponseEnvelope resp)
    {
        JsonElement? body = resp.JsonBody.HasValue && resp.JsonBody.Value.ValueKind == JsonValueKind.Object
            ? resp.JsonBody.Value
            : null;

        if (body.HasValue && BuildError(body.Value, resp) is AudDApiException error)
        {
            throw error;
        }
        if (resp.HttpStatus >= HttpClientErrorFloor)
        {
            throw new AudDServerException(
                errorCode: 0,
                serverMessage: $"Longpoll endpoint returned HTTP {resp.HttpStatus}",
                httpStatus: resp.HttpStatus,
                requestId: resp.RequestId);
        }
        return body.HasValue ? Read(body.Value) : LongpollBatch.Unrecognized;
    }

    /// <summary>Read a successful (non-error) longpoll body.</summary>
    internal static LongpollBatch Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return LongpollBatch.Unrecognized;

        if (body.TryGetProperty("events", out var events))
        {
            if (events.ValueKind != JsonValueKind.Array) return LongpollBatch.Unrecognized;
            var delivered = new List<CallbackEvent>();
            long? newest = null;
            foreach (var ev in events.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object) continue;
                if (ev.TryGetProperty("timestamp", out var ts) && ReadTimestamp(ts) is long t)
                {
                    newest = newest is long n ? Math.Max(n, t) : t;
                }
                if (ev.TryGetProperty("data", out var data) && ParseEvent(data) is CallbackEvent parsed)
                {
                    delivered.Add(parsed);
                }
            }
            return new LongpollBatch(delivered, newest, recognized: true);
        }

        if (body.TryGetProperty("result", out _) || body.TryGetProperty("notification", out _))
        {
            IReadOnlyList<CallbackEvent> single = ParseEvent(body) is CallbackEvent parsed
                ? new[] { parsed }
                : Array.Empty<CallbackEvent>();
            return new LongpollBatch(single, TopLevelTimestamp(body), recognized: true);
        }

        if (body.TryGetProperty("timeout", out _))
        {
            return new LongpollBatch(Array.Empty<CallbackEvent>(), TopLevelTimestamp(body), recognized: true);
        }

        return LongpollBatch.Unrecognized;
    }

    private static AudDApiException? BuildError(JsonElement body, HttpResponseEnvelope resp)
    {
        if (body.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
        {
            return new AudDInvalidRequestException(
                errorCode: 0,
                serverMessage: err.GetString() ?? "",
                httpStatus: resp.HttpStatus,
                requestId: resp.RequestId,
                rawResponse: body);
        }
        var isStatusError = body.TryGetProperty("status", out var st)
            && st.ValueKind == JsonValueKind.String
            && string.Equals(st.GetString(), "error", StringComparison.Ordinal);
        if (isStatusError || err.ValueKind == JsonValueKind.Object)
        {
            return ErrorRaiser.BuildFromErrorBody(body, resp.HttpStatus, resp.RequestId);
        }
        return null;
    }

    /// <summary>
    /// Parse one event's <c>data</c> with the callback parser. Returns null for data
    /// that can't be parsed and for a recognition with no candidate songs.
    /// </summary>
    private static CallbackEvent? ParseEvent(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.String)
        {
            // `data` delivered as JSON text rather than an object.
            try
            {
                using var doc = JsonDocument.Parse(data.GetString() ?? "");
                data = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        if (data.ValueKind != JsonValueKind.Object) return null;

        CallbackEvent ev;
        try
        {
            ev = AudDHelpers.ParseCallback(data);
        }
        catch (Exception ex) when (ex is AudDException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
        if (ev is CallbackEvent.Match && !HasCandidate(data)) return null;
        return ev;
    }

    /// <summary>True when <c>result.results</c> holds at least one song object.</summary>
    private static bool HasCandidate(JsonElement data)
    {
        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return false;
        if (!result.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return false;
        foreach (var r in results.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object) return true;
        }
        return false;
    }

    private static long? TopLevelTimestamp(JsonElement body)
        => body.TryGetProperty("timestamp", out var ts) ? ReadTimestamp(ts) : null;

    /// <summary>A millisecond timestamp sent as a number or a numeric string; anything else reads as absent.</summary>
    private static long? ReadTimestamp(JsonElement value)
        => value.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? TolerantParser.CoerceToInt64(value)
            : null;
}
