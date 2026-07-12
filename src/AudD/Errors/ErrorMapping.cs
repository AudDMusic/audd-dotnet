using System.Text.Json;

namespace AudD;

/// <summary>
/// Code → exception-class lookup for AudD error codes. Public + extensible: users
/// can register handlers for new codes without waiting for an SDK release.
/// </summary>
public static class AudDErrorMap
{
    /// <summary>Factory that builds the appropriate <see cref="AudDApiException"/> for a code.</summary>
    public delegate AudDApiException Factory(
        int errorCode,
        string serverMessage,
        int httpStatus,
        string? requestId,
        IReadOnlyDictionary<string, JsonElement>? requestedParams,
        string? requestMethod,
        string? brandedMessage,
        JsonElement rawResponse);

    private static readonly Dictionary<int, Factory> _map = new()
    {
        [900] = (c, m, h, rid, rp, rm, bm, rr) => new AudDAuthenticationException(c, m, h, rid, rp, rm, bm, rr),
        [901] = (c, m, h, rid, rp, rm, bm, rr) => new AudDAuthenticationException(c, m, h, rid, rp, rm, bm, rr),
        [903] = (c, m, h, rid, rp, rm, bm, rr) => new AudDAuthenticationException(c, m, h, rid, rp, rm, bm, rr),
        [902] = (c, m, h, rid, rp, rm, bm, rr) => new AudDQuotaException(c, m, h, rid, rp, rm, bm, rr),
        [904] = (c, m, h, rid, rp, rm, bm, rr) => new AudDSubscriptionException(c, m, h, rid, rp, rm, bm, rr),
        [905] = (c, m, h, rid, rp, rm, bm, rr) => new AudDSubscriptionException(c, m, h, rid, rp, rm, bm, rr),
        [50] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [51] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [600] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [601] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [602] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [700] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [701] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [702] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [906] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidRequestException(c, m, h, rid, rp, rm, bm, rr),
        [300] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidAudioException(c, m, h, rid, rp, rm, bm, rr),
        [400] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidAudioException(c, m, h, rid, rp, rm, bm, rr),
        [500] = (c, m, h, rid, rp, rm, bm, rr) => new AudDInvalidAudioException(c, m, h, rid, rp, rm, bm, rr),
        [610] = (c, m, h, rid, rp, rm, bm, rr) => new AudDStreamLimitException(c, m, h, rid, rp, rm, bm, rr),
        [611] = (c, m, h, rid, rp, rm, bm, rr) => new AudDRateLimitException(c, m, h, rid, rp, rm, bm, rr),
        [907] = (c, m, h, rid, rp, rm, bm, rr) => new AudDNotReleasedException(c, m, h, rid, rp, rm, bm, rr),
        [19] = (c, m, h, rid, rp, rm, bm, rr) => new AudDBlockedException(c, m, h, rid, rp, rm, bm, rr),
        [31337] = (c, m, h, rid, rp, rm, bm, rr) => new AudDBlockedException(c, m, h, rid, rp, rm, bm, rr),
        [20] = (c, m, h, rid, rp, rm, bm, rr) => new AudDNeedsUpdateException(c, m, h, rid, rp, rm, bm, rr),
        [100] = (c, m, h, rid, rp, rm, bm, rr) => new AudDServerException(c, m, h, rid, rp, rm, bm, rr),
        [1000] = (c, m, h, rid, rp, rm, bm, rr) => new AudDServerException(c, m, h, rid, rp, rm, bm, rr),
    };

    /// <summary>Register or replace a factory for a given AudD error code.</summary>
    public static void Register(int errorCode, Factory factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        lock (_map)
        {
            _map[errorCode] = factory;
        }
    }

    /// <summary>Look up the factory for a code, falling back to a generic <see cref="AudDServerException"/>.</summary>
    public static Factory FactoryFor(int code)
    {
        lock (_map)
        {
            if (_map.TryGetValue(code, out var f)) return f;
        }
        return DefaultFactory;
    }

    internal static readonly Factory DefaultFactory = (c, m, h, rid, rp, rm, bm, rr)
        => new AudDServerException(c, m, h, rid, rp, rm, bm, rr);
}

internal static class ErrorRaiser
{
    public static AudDApiException BuildFromErrorBody(
        JsonElement body,
        int httpStatus,
        string? requestId,
        bool customCatalogContext = false)
    {
        int code = 0;
        string message = "";
        if (body.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            if (err.TryGetProperty("error_code", out var c))
            {
                code = DecodeErrorCode(c);
            }
            if (err.TryGetProperty("error_message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString() ?? "";
            }
        }

        IReadOnlyDictionary<string, JsonElement>? requestedParams = null;
        if (body.TryGetProperty("request_params", out var rp1) && rp1.ValueKind == JsonValueKind.Object)
        {
            requestedParams = ToDict(rp1);
        }
        else if (body.TryGetProperty("requested_params", out var rp2) && rp2.ValueKind == JsonValueKind.Object)
        {
            requestedParams = ToDict(rp2);
        }

        string? requestMethod = null;
        if (body.TryGetProperty("request_api_method", out var rm) && rm.ValueKind == JsonValueKind.String)
        {
            requestMethod = rm.GetString();
        }

        string? branded = null;
        if (body.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
        {
            string? artist = resultEl.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            string? title = resultEl.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
            {
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                    branded = $"{artist} — {title}";
                else
                    branded = artist ?? title;
            }
        }

        if (customCatalogContext && (code == 904 || code == 905))
        {
            return new AudDCustomCatalogAccessException(code, message, httpStatus, requestId, requestedParams, requestMethod, branded, body);
        }

        var factory = AudDErrorMap.FactoryFor(code);
        return factory(code, message, httpStatus, requestId, requestedParams, requestMethod, branded, body);
    }

    /// <summary>
    /// Decode an <c>error_code</c> element without throwing. Accepts an integer
    /// number, a numeric string, or a fractional number (truncated); anything else
    /// (bool, object, array, non-numeric string) decodes to 0.
    /// </summary>
    internal static int DecodeErrorCode(JsonElement c)
    {
        switch (c.ValueKind)
        {
            case JsonValueKind.Number:
                if (c.TryGetInt32(out var i)) return i;
                if (c.TryGetDouble(out var d)) return (int)d;
                return 0;
            case JsonValueKind.String:
                return int.TryParse(c.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
            default:
                return 0;
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ToDict(JsonElement obj)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var p in obj.EnumerateObject())
        {
            d[p.Name] = p.Value.Clone();
        }
        return d;
    }
}
