namespace AudD.Internal;

/// <summary>
/// Determines which conditions are retryable for a given endpoint.
/// </summary>
internal enum RetryClass
{
    /// <summary>Idempotent reads (streams.list, getCallbackUrl): retry on 408/429/5xx + any connection error.</summary>
    Read,

    /// <summary>recognize/recognize_enterprise/find_lyrics: retry on pre-upload connection failures + 5xx.
    /// Do NOT retry on read-timeout-after-upload (cost protection).</summary>
    Recognition,

    /// <summary>addStream/setUrl/deleteStream/setCallbackUrl/upload: retry only on pre-upload connection failures.
    /// Do NOT retry on 5xx (the side effect may have happened).</summary>
    Mutating,
}

internal sealed record RetryPolicy(
    RetryClass RetryClass,
    int MaxAttempts = 3,
    double BackoffFactor = 0.5,
    double BackoffMaxSeconds = 30.0);

internal static class RetryClassifier
{
    private const int HttpRequestTimeout = 408;
    private const int HttpTooManyRequests = 429;
    private const int HttpServerErrorFloor = 500;

    public static bool ShouldRetryStatus(int httpStatus, RetryClass cls) => cls switch
    {
        RetryClass.Read => httpStatus is HttpRequestTimeout or HttpTooManyRequests
                          || httpStatus >= HttpServerErrorFloor,
        RetryClass.Recognition => httpStatus >= HttpServerErrorFloor,
        RetryClass.Mutating => false,
        _ => false,
    };

    public static bool ShouldRetryException(Exception exc, RetryClass cls) => cls switch
    {
        RetryClass.Read => IsTransportException(exc),
        RetryClass.Recognition => IsPreUploadConnectionException(exc),
        RetryClass.Mutating => IsPreUploadConnectionException(exc),
        _ => false,
    };

    /// <summary>
    /// Connection errors raised before the request body finished uploading.
    /// HttpRequestException covers DNS / TCP / TLS handshake failures pre-upload.
    /// We treat all HttpRequestExceptions as "pre-upload" — recognition retries are
    /// pre-call (per the design); read-timeout-after-upload manifests as a different
    /// path we deliberately do not classify as retryable here.
    /// </summary>
    public static bool IsPreUploadConnectionException(Exception exc)
    {
        if (exc is HttpRequestException) return true;
        if (exc is OperationCanceledException) return false; // user-initiated cancel
        return false;
    }

    public static bool IsTransportException(Exception exc)
    {
        if (exc is HttpRequestException) return true;
        if (exc is TaskCanceledException) return true; // typically read-timeout
        return false;
    }
}
