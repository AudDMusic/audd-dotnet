using AudD.Internal;
using Microsoft.Extensions.Logging;

namespace AudD;

/// <summary>
/// Custom-catalog endpoint. <b>NOT for music recognition.</b> See <see cref="AddAsync(long, string, CancellationToken)"/>.
/// Reach via <c>audd.CustomCatalog.AddAsync(...)</c>.
/// </summary>
public sealed class CustomCatalog
{
    private const string UploadUrl = "https://api.audd.io/upload/";

    private readonly HttpTransport _http;
    private readonly double _backoffFactor;
    private readonly ILogger _logger;
    private readonly EventEmitter _events;

    // maxRetries is accepted for signature parity with the other namespace classes
    // (Streams, Advanced) but deliberately ignored: custom-catalog upload is metered
    // and must not auto-retry — see AddAsync for the policy override.
    internal CustomCatalog(HttpTransport http, int maxRetries, double backoffFactor, ILogger logger, EventEmitter? events = null)
    {
        _ = maxRetries;
        _http = http;
        _backoffFactor = backoffFactor;
        _logger = logger;
        _events = events ?? EventEmitter.Disabled;
    }

    /// <summary>
    /// <b>This is NOT how you submit audio for music recognition.</b> For recognition,
    /// use <see cref="AudD.RecognizeAsync(string, IEnumerable{string}?, string?, TimeSpan?, CancellationToken)"/>
    /// (or <see cref="AudD.RecognizeEnterpriseAsync(string, IEnumerable{string}?, int?, int?, int?, int?, bool?, bool?, TimeSpan?, CancellationToken)"/>
    /// for files longer than 25 seconds). This method adds a song to your <b>private
    /// fingerprint catalog</b> so AudD's recognition can later identify <i>your own</i>
    /// tracks for <i>your account only</i>. Requires special access — contact api@audd.io
    /// if you need it enabled.
    ///
    /// Calling this again with the same <paramref name="audioId"/> re-fingerprints that slot.
    /// There is no public list/delete endpoint; track audio_id ↔ song mappings on your side.
    /// </summary>
    public Task AddAsync(long audioId, string sourceUrlOrPath, CancellationToken cancellationToken = default)
        => AddAsync(audioId, RecognitionSource.From(sourceUrlOrPath), cancellationToken);

    /// <inheritdoc cref="AddAsync(long, string, CancellationToken)"/>
    public Task AddAsync(long audioId, FileInfo file, CancellationToken cancellationToken = default)
        => AddAsync(audioId, RecognitionSource.From(file), cancellationToken);

    /// <inheritdoc cref="AddAsync(long, string, CancellationToken)"/>
    public Task AddAsync(long audioId, byte[] bytes, CancellationToken cancellationToken = default)
        => AddAsync(audioId, RecognitionSource.From(bytes), cancellationToken);

    /// <inheritdoc cref="AddAsync(long, string, CancellationToken)"/>
    public Task AddAsync(long audioId, System.IO.Stream stream, CancellationToken cancellationToken = default)
        => AddAsync(audioId, RecognitionSource.From(stream), cancellationToken);

    private async Task AddAsync(long audioId, RecognitionSource source, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["audio_id"] = audioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        // Custom-catalog upload is metered per call. A retry on a transient pre-upload
        // failure could double-charge for the same fingerprinting work (the server may
        // already have begun ingesting bytes when the connection drops). Force a
        // 1-attempt policy regardless of the user's configured maxRetries so transient
        // failures surface as clean exceptions instead of silent re-uploads. Streams
        // mutating ops (addStream/setUrl/deleteStream/setCallbackUrl) keep the standard
        // RetryClass.Mutating policy because they are server-idempotent on radioId.
        var policy = new RetryPolicy(RetryClass.Mutating, MaxAttempts: 1, _backoffFactor);
        var startedAt = DateTime.UtcNow;
        _events.EmitRequest("custom_catalog_add", UploadUrl);
        try
        {
            var resp = await Retry.RunAsync(
                ct => _http.PostFormAsync(
                    UploadUrl,
                    extra => source.BuildContent(extra),
                    fields,
                    ct),
                policy,
                cancellationToken).ConfigureAwait(false);
            ResponseDecoder.DecodeOrThrow(resp, _logger, customCatalogContext: true);
            _events.EmitResponse("custom_catalog_add", UploadUrl, resp.RequestId, resp.HttpStatus, DateTime.UtcNow - startedAt);
        }
        catch (HttpRequestException exc)
        {
            _events.EmitException("custom_catalog_add", UploadUrl, exc, DateTime.UtcNow - startedAt);
            throw new AudDConnectionException(exc.Message, exc);
        }
        catch (AudDApiException exc)
        {
            _events.EmitException("custom_catalog_add", UploadUrl, exc, DateTime.UtcNow - startedAt,
                httpStatus: exc.HttpStatus, requestId: exc.RequestId, errorCode: exc.ErrorCode);
            throw;
        }
        catch (Exception exc)
        {
            _events.EmitException("custom_catalog_add", UploadUrl, exc, DateTime.UtcNow - startedAt);
            throw;
        }
    }
}
