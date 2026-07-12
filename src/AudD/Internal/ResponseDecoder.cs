using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudD.Internal;

/// <summary>
/// Centralized response decoder. Mirrors audd-python's _decode_or_raise.
///
/// Distinguishes:
/// <list type="bullet">
///   <item>non-2xx HTTP with non-JSON body → <see cref="AudDServerException"/> preserving status</item>
///   <item>2xx with non-JSON body → <see cref="AudDSerializationException"/></item>
///   <item>status=error with code-51 + result → log warning, strip error, fall through</item>
///   <item>status=error otherwise → typed <see cref="AudDApiException"/> via <see cref="AudDErrorMap"/></item>
///   <item>status=success → return body as <see cref="JsonElement"/></item>
/// </list>
/// </summary>
internal static class ResponseDecoder
{
    private const int DeprecatedParamsCode = 51;
    private const int HttpClientErrorFloor = 400;

    public static JsonElement DecodeOrThrow(
        HttpResponseEnvelope resp,
        ILogger logger,
        bool customCatalogContext = false)
    {
        if (!resp.JsonBody.HasValue || resp.JsonBody.Value.ValueKind != JsonValueKind.Object)
        {
            if (resp.HttpStatus >= HttpClientErrorFloor)
            {
                throw new AudDServerException(
                    errorCode: 0,
                    serverMessage: $"HTTP {resp.HttpStatus} with non-JSON response body",
                    httpStatus: resp.HttpStatus,
                    requestId: resp.RequestId);
            }
            throw new AudDSerializationException("Unparseable response", resp.RawText);
        }

        var body = resp.JsonBody.Value;

        // Code-51 deprecation pass-through: server sends status=error + a usable result.
        if (IsDeprecationPassThrough(body))
        {
            var msg = body.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                ? (err.TryGetProperty("error_message", out var em) && em.ValueKind == JsonValueKind.String
                    ? em.GetString() ?? "Deprecated parameter used"
                    : "Deprecated parameter used")
                : "Deprecated parameter used";
            logger.LogWarning("AudD deprecation (code 51): {Message}", msg);
            return RewriteAsSuccess(body);
        }

        var status = body.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()
            : null;

        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            throw ErrorRaiser.BuildFromErrorBody(body, resp.HttpStatus, resp.RequestId, customCatalogContext);
        }

        if (string.Equals(status, "success", StringComparison.Ordinal))
        {
            return body;
        }

        throw new AudDServerException(
            errorCode: 0,
            serverMessage: $"Unexpected response status: {status ?? "null"}",
            httpStatus: resp.HttpStatus,
            requestId: resp.RequestId,
            rawResponse: body);
    }

    private static bool IsDeprecationPassThrough(JsonElement body)
    {
        if (!body.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object) return false;
        if (!err.TryGetProperty("error_code", out var c)) return false;
        var code = ErrorRaiser.DecodeErrorCode(c);
        if (code != DeprecatedParamsCode) return false;
        if (!body.TryGetProperty("result", out var r)) return false;
        return r.ValueKind != JsonValueKind.Null;
    }

    private static JsonElement RewriteAsSuccess(JsonElement original)
    {
        // Re-serialize without `error`, with status=success.
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in original.EnumerateObject())
            {
                if (string.Equals(p.Name, "error", StringComparison.Ordinal)) continue;
                if (string.Equals(p.Name, "status", StringComparison.Ordinal)) continue;
                p.WriteTo(w);
            }
            w.WriteString("status", "success");
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }
}
