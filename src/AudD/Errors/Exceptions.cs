using System.Text.Json;

namespace AudD;

/// <summary>Base exception for everything raised by this SDK.</summary>
public class AudDException : Exception
{
    /// <inheritdoc/>
    public AudDException(string message) : base(message) { }

    /// <inheritdoc/>
    public AudDException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Server returned <c>status=error</c>. Carries the AudD error code + the full echo.</summary>
public class AudDApiException : AudDException
{
    /// <summary>AudD numeric error code (e.g. 900, 904, 51).</summary>
    public int ErrorCode { get; }

    /// <summary>Server's human-readable message.</summary>
    public string ServerMessage { get; }

    /// <summary>HTTP status code carrying the response.</summary>
    public int HttpStatus { get; }

    /// <summary>Value of the <c>x-request-id</c> header, when the server returned one.</summary>
    public string? RequestId { get; }

    /// <summary>Server-side echoed request parameters (handles both <c>request_params</c> and <c>requested_params</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement> RequestedParams { get; }

    /// <summary>Server's humanized method label (informational only).</summary>
    public string? RequestMethod { get; }

    /// <summary>Branded server-side denial text (e.g. "Sorry, your IP was banned"), null if absent.</summary>
    public string? BrandedMessage { get; }

    /// <summary>Full unparsed response body — JSON object as a <see cref="JsonElement"/>.</summary>
    public JsonElement RawResponse { get; }

    /// <inheritdoc/>
    public AudDApiException(
        int errorCode,
        string serverMessage,
        int httpStatus,
        string? requestId = null,
        IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null,
        string? brandedMessage = null,
        JsonElement rawResponse = default)
        : base(FormatMessage(errorCode, serverMessage))
    {
        ErrorCode = errorCode;
        ServerMessage = serverMessage;
        HttpStatus = httpStatus;
        RequestId = requestId;
        RequestedParams = requestedParams ?? EmptyParams;
        RequestMethod = requestMethod;
        BrandedMessage = brandedMessage;
        RawResponse = rawResponse;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyParams =
        new Dictionary<string, JsonElement>(0);

    private static string FormatMessage(int code, string msg)
        => $"[#{code}] {msg}";
}

/// <summary>900 / 901 / 903 — token problems.</summary>
public sealed class AudDAuthenticationException : AudDApiException
{
    /// <inheritdoc/>
    public AudDAuthenticationException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>902 — quota / per-copy limit reached.</summary>
public sealed class AudDQuotaException : AudDApiException
{
    /// <inheritdoc/>
    public AudDQuotaException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>904 / 905 — endpoint not available with this token.</summary>
public class AudDSubscriptionException : AudDApiException
{
    /// <inheritdoc/>
    public AudDSubscriptionException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>904 raised specifically from <c>custom_catalog.*</c>. Overrides the user-facing message.</summary>
public sealed class AudDCustomCatalogAccessException : AudDSubscriptionException
{
    /// <summary>The original server-side message, preserved for ticket-grepping.</summary>
    public string OriginalServerMessage { get; }

    /// <inheritdoc/>
    public AudDCustomCatalogAccessException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, BuildOverriddenMessage(serverMessage), httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
        OriginalServerMessage = serverMessage;
    }

    private static string BuildOverriddenMessage(string original) =>
        "Adding songs to your custom catalog requires enterprise access that isn't enabled on your account.\n\n" +
        "Note: the custom-catalog endpoint is for adding songs to your private fingerprint database, not for music recognition. " +
        "If you intended to identify music, use RecognizeAsync(...) (or RecognizeEnterpriseAsync(...) for files longer than 25 seconds) instead.\n\n" +
        "To request custom-catalog access, contact api@audd.io.\n\n" +
        $"[Server message: {original}]";
}

/// <summary>50 / 51 / 600-602 / 700-702 / 906 — bad input from the caller.</summary>
public sealed class AudDInvalidRequestException : AudDApiException
{
    /// <inheritdoc/>
    public AudDInvalidRequestException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>300 / 400 / 500 — caller's audio file is the problem.</summary>
public sealed class AudDInvalidAudioException : AudDApiException
{
    /// <inheritdoc/>
    public AudDInvalidAudioException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>611 — per-stream daily rate limit (and HTTP 429).</summary>
public sealed class AudDRateLimitException : AudDApiException
{
    /// <inheritdoc/>
    public AudDRateLimitException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>610 — subscription stream slots exhausted.</summary>
public sealed class AudDStreamLimitException : AudDApiException
{
    /// <inheritdoc/>
    public AudDStreamLimitException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>907 — song has not been released yet.</summary>
public sealed class AudDNotReleasedException : AudDApiException
{
    /// <inheritdoc/>
    public AudDNotReleasedException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>19 family + 31337 — security / abuse / sanctions / IP ban / maintenance.</summary>
public sealed class AudDBlockedException : AudDApiException
{
    /// <inheritdoc/>
    public AudDBlockedException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>20 — app needs update / paid version required.</summary>
public sealed class AudDNeedsUpdateException : AudDApiException
{
    /// <inheritdoc/>
    public AudDNeedsUpdateException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>100 / 1000 / unknown codes / generic upstream failures (non-2xx HTTP without recognizable JSON).</summary>
public sealed class AudDServerException : AudDApiException
{
    /// <inheritdoc/>
    public AudDServerException(int errorCode, string serverMessage, int httpStatus,
        string? requestId = null, IReadOnlyDictionary<string, JsonElement>? requestedParams = null,
        string? requestMethod = null, string? brandedMessage = null, JsonElement rawResponse = default)
        : base(errorCode, serverMessage, httpStatus, requestId, requestedParams, requestMethod, brandedMessage, rawResponse)
    {
    }
}

/// <summary>Network / TLS / timeout — no response received.</summary>
public sealed class AudDConnectionException : AudDException
{
    /// <inheritdoc/>
    public AudDConnectionException(string message, Exception? inner = null)
        : base(message, inner ?? new Exception(message))
    {
    }
}

/// <summary>2xx response with malformed JSON body.</summary>
public sealed class AudDSerializationException : AudDException
{
    /// <summary>Raw response body as text, when available.</summary>
    public string RawText { get; }

    /// <inheritdoc/>
    public AudDSerializationException(string message, string rawText = "")
        : base(message)
    {
        RawText = rawText;
    }
}
