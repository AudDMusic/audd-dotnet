using Microsoft.Extensions.Logging;

namespace AudD;

/// <summary>Inspection event lifecycle stage.</summary>
public enum AudDEventKind
{
    /// <summary>Emitted just before the SDK begins issuing the HTTP request.</summary>
    Request,

    /// <summary>Emitted after the SDK receives and decodes the HTTP response.</summary>
    Response,

    /// <summary>Emitted when the request path raises (HTTP error, timeout, parse failure, etc.).</summary>
    Exception,
}

/// <summary>
/// Inspection event emitted by the SDK request lifecycle. Frozen, plain-data;
/// <b>never</b> includes the api_token or request body bytes.
///
/// <para>Hooks receive these via the <c>onEvent</c> constructor option on
/// <see cref="AudD"/>. Hooks must not throw; exceptions are caught and logged
/// at debug-level via <see cref="ILogger"/>.</para>
/// </summary>
/// <param name="Kind">Lifecycle stage (<see cref="AudDEventKind.Request"/>, <see cref="AudDEventKind.Response"/>, or <see cref="AudDEventKind.Exception"/>).</param>
/// <param name="Method">AudD method name, e.g. <c>"recognize"</c>, <c>"addStream"</c>.</param>
/// <param name="Url">Full URL the SDK targeted (no api_token in query string).</param>
/// <param name="RequestId">Value of the <c>x-request-id</c> response header, when present.</param>
/// <param name="HttpStatus">HTTP status from the response, when one was received.</param>
/// <param name="Elapsed">Wall-clock time from request start to event emission.</param>
/// <param name="ErrorCode">AudD error code, when this event represents an error response.</param>
/// <param name="Extras">Free-form per-event extras the SDK may attach (never sensitive data).</param>
public sealed record AudDEvent(
    AudDEventKind Kind,
    string Method,
    string Url,
    string? RequestId,
    int? HttpStatus,
    TimeSpan? Elapsed,
    int? ErrorCode,
    IReadOnlyDictionary<string, object?> Extras);
