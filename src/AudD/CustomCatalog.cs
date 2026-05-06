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
    private readonly int _maxRetries;
    private readonly double _backoffFactor;
    private readonly ILogger _logger;
    private readonly EventEmitter _events;

    internal CustomCatalog(HttpTransport http, int maxRetries, double backoffFactor, ILogger logger, EventEmitter? events = null)
    {
        _http = http;
        _maxRetries = maxRetries;
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
        var policy = new RetryPolicy(RetryClass.Mutating, _maxRetries, _backoffFactor);
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
