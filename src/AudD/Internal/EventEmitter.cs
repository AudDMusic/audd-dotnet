using Microsoft.Extensions.Logging;

namespace AudD.Internal;

/// <summary>
/// Wraps an optional <c>Action&lt;AudDEvent&gt;</c> so the request path can emit
/// inspection events without throwing into user-supplied hook code. Hook
/// exceptions are caught and logged at debug-level so observability never
/// breaks a request.
/// </summary>
internal sealed class EventEmitter
{
    private readonly Action<AudDEvent>? _hook;
    private readonly ILogger? _logger;

    public static readonly EventEmitter Disabled = new(null, null);

    public EventEmitter(Action<AudDEvent>? hook, ILogger? logger)
    {
        _hook = hook;
        _logger = logger;
    }

    public bool IsEnabled => _hook is not null;

    public void EmitRequest(string method, string url, IReadOnlyDictionary<string, object?>? extras = null)
    {
        if (_hook is null) return;
        SafeEmit(new AudDEvent(
            AudDEventKind.Request, method, url,
            RequestId: null, HttpStatus: null, Elapsed: null, ErrorCode: null,
            Extras: extras ?? Empty));
    }

    public void EmitResponse(string method, string url, string? requestId, int? httpStatus, TimeSpan elapsed,
        int? errorCode = null, IReadOnlyDictionary<string, object?>? extras = null)
    {
        if (_hook is null) return;
        SafeEmit(new AudDEvent(
            AudDEventKind.Response, method, url,
            requestId, httpStatus, elapsed, errorCode,
            extras ?? Empty));
    }

    public void EmitException(string method, string url, Exception exc, TimeSpan elapsed,
        int? httpStatus = null, string? requestId = null, int? errorCode = null)
    {
        if (_hook is null) return;
        var extras = new Dictionary<string, object?>
        {
            ["exception_type"] = exc.GetType().FullName,
            ["exception_message"] = exc.Message,
        };
        SafeEmit(new AudDEvent(
            AudDEventKind.Exception, method, url,
            requestId, httpStatus, elapsed, errorCode,
            extras));
    }

    private void SafeEmit(AudDEvent ev)
    {
        try
        {
            _hook?.Invoke(ev);
        }
        catch (Exception exc)
        {
            // Observability hooks must never break the request path.
            _logger?.LogDebug(exc, "AudD onEvent hook raised; suppressed");
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>(0);
}
