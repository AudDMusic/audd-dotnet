using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudD;

/// <summary>Pure helpers — no HTTP, no SDK state.</summary>
public static class AudDHelpers
{
    /// <summary>
    /// Compute the 9-char longpoll category locally from token + radio_id.
    ///
    /// Formula (per docs.audd.io/streams.md): hex-MD5 of (hex-MD5 of api_token,
    /// concatenated with the radio_id rendered as a decimal string), truncated to
    /// the first 9 hex chars.
    /// </summary>
    public static string DeriveLongpollCategory(string apiToken, long radioId)
    {
        if (apiToken is null) throw new ArgumentNullException(nameof(apiToken));
#if NET6_0_OR_GREATER
        var inner = ToHex(MD5.HashData(Encoding.UTF8.GetBytes(apiToken)));
        var full = ToHex(MD5.HashData(Encoding.UTF8.GetBytes(inner + radioId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
#else
        using var md5 = MD5.Create();
        var inner = ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes(apiToken)));
        var full = ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes(inner + radioId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
#endif
        return full.Substring(0, 9);
    }

    /// <summary>
    /// Append a <c>return=&lt;metadata&gt;</c> query parameter to a callback URL.
    /// If the URL already has a <c>return</c> parameter, throws
    /// <see cref="AudDInvalidRequestException"/> rather than silently overwriting.
    /// </summary>
    public static string AddReturnToUrl(string url, string? returnMetadata)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));
        if (returnMetadata is null) return url;
        return AddReturnToUrlCore(url, returnMetadata);
    }

    /// <summary>Convenience overload accepting a list of return values.</summary>
    public static string AddReturnToUrl(string url, IEnumerable<string>? returnMetadata)
    {
        if (returnMetadata is null) return url;
        return AddReturnToUrlCore(url, string.Join(",", returnMetadata));
    }

    private static string AddReturnToUrlCore(string url, string metadata)
    {
        var u = new UriBuilder(url);
        var existing = ParseQuery(u.Query);
        foreach (var (k, _) in existing)
        {
            if (string.Equals(k, "return", StringComparison.Ordinal))
            {
                throw new AudDInvalidRequestException(
                    errorCode: 0,
                    serverMessage: "URL already contains a `return` query parameter; pass returnMetadata=null or remove the parameter from the URL — refusing to silently overwrite.",
                    httpStatus: 0);
            }
        }
        existing.Add(("return", metadata));
        u.Query = BuildQuery(existing);
        return u.ToString();
    }

    private static List<(string, string)> ParseQuery(string query)
    {
        var output = new List<(string, string)>();
        if (string.IsNullOrEmpty(query)) return output;
        var s = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        if (s.Length == 0) return output;
        foreach (var piece in s.Split('&'))
        {
            if (piece.Length == 0) continue;
            var eq = piece.IndexOf('=');
            if (eq < 0)
            {
                output.Add((Uri.UnescapeDataString(piece), ""));
            }
            else
            {
                output.Add((Uri.UnescapeDataString(piece.Substring(0, eq)),
                            Uri.UnescapeDataString(piece.Substring(eq + 1))));
            }
        }
        return output;
    }

    private static string BuildQuery(List<(string, string)> kvs)
    {
        if (kvs.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        bool first = true;
        foreach (var (k, v) in kvs)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(k));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse an enterprise chunk <c>offset</c> into seconds. Accepts
    /// <c>"SS"</c>, <c>"MM:SS"</c>, <c>"HH:MM:SS"</c>, or a bare number
    /// (seconds, possibly fractional). Returns <c>null</c> on null/empty or
    /// unparseable input; never throws. Parsing is invariant-culture.
    /// </summary>
    internal static double? OffsetToSeconds(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset)) return null;
        var parts = offset!.Split(':');
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return null;
            }
            total = (total * 60) + v;
        }
        return total;
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Parse a streams callback POST body into a typed event. Recognition callbacks
    /// have an outer <c>result</c> block; notification callbacks have a
    /// <c>notification</c> block; the discrimination is by-key.
    ///
    /// <para>Returns one of the <see cref="CallbackEvent"/> derived records:
    /// <see cref="CallbackEvent.Match"/> or <see cref="CallbackEvent.Notification"/>.</para>
    /// </summary>
    public static CallbackEvent ParseCallback(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new AudDSerializationException("Callback body is not a JSON object");

        if (body.TryGetProperty("notification", out var nf) && nf.ValueKind == JsonValueKind.Object)
        {
            var notif = nf.Deserialize(AudDJsonContext.Default.StreamCallbackNotification)
                        ?? new StreamCallbackNotification();
            long? time = null;
            if (body.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                time = t.GetInt64();
            return new CallbackEvent.Notification(notif with { Time = time, RawResponse = body });
        }

        if (body.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
        {
            StreamCallbackMatch? match;
            try
            {
                match = resultEl.Deserialize(AudDJsonContext.Default.StreamCallbackMatch);
            }
            catch (JsonException ex)
            {
                throw new AudDSerializationException("callback result: " + ex.Message, body.GetRawText());
            }
            if (match is null)
                throw new AudDSerializationException("callback result deserialized to null", body.GetRawText());
            return new CallbackEvent.Match(match with { RawResponse = body });
        }

        throw new AudDSerializationException("callback body has neither result nor notification", body.GetRawText());
    }

    /// <summary>Parse a streams callback POST body from JSON text.</summary>
    public static CallbackEvent ParseCallback(string body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AudDSerializationException("callback body is not valid JSON: " + ex.Message, body);
        }
        using (doc)
        {
            return ParseCallback(doc.RootElement.Clone());
        }
    }

    /// <summary>
    /// Read a streams-callback POST body from <paramref name="bodyStream"/> and parse it.
    /// Designed for use from any HTTP framework — pass <c>HttpRequest.Body</c>
    /// (ASP.NET Core), <c>req.InputStream</c> (HttpListener), or any other source.
    /// Does not close the stream.
    /// </summary>
    public static async Task<CallbackEvent> HandleCallbackAsync(System.IO.Stream bodyStream, CancellationToken cancellationToken = default)
    {
        if (bodyStream is null) throw new ArgumentNullException(nameof(bodyStream));
        using var reader = new System.IO.StreamReader(bodyStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParseCallback(text);
    }

    /// <summary>
    /// Synchronous overload of <see cref="HandleCallbackAsync"/> for callers reading
    /// raw bytes (queue consumers, replay tools).
    /// </summary>
    public static CallbackEvent HandleCallback(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new AudDSerializationException("callback body is empty");
        var text = System.Text.Encoding.UTF8.GetString(body);
        return ParseCallback(text);
    }
}
