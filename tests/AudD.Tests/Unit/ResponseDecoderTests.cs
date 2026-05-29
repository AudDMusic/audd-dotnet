using System.Text.Json;
using AudD;
using AudD.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AudD.Tests.Unit;

public class ResponseDecoderTests
{
    private static HttpResponseEnvelope Env(string json, int status = 200) =>
        new()
        {
            JsonBody = ParseJson(json),
            HttpStatus = status,
            RequestId = null,
            RawText = json,
        };

    private static HttpResponseEnvelope NonJson(int status, string raw) =>
        new()
        {
            JsonBody = null,
            HttpStatus = status,
            RequestId = null,
            RawText = raw,
        };

    private static JsonElement? ParseJson(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    [Fact]
    public void Success_PassesThroughBody()
    {
        var body = ResponseDecoder.DecodeOrThrow(Env("""{"status":"success","result":{"x":1}}"""), NullLogger.Instance);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
    }

    [Fact]
    public void Error_RaisesTypedException()
    {
        var json = """{"status":"error","error":{"error_code":900,"error_message":"bad"}}""";
        Assert.Throws<AudDAuthenticationException>(() =>
            ResponseDecoder.DecodeOrThrow(Env(json), NullLogger.Instance));
    }

    [Fact]
    public void NonJson_With5xx_RaisesAudDServerException()
    {
        var ex = Assert.Throws<AudDServerException>(() =>
            ResponseDecoder.DecodeOrThrow(NonJson(502, "<html>Bad Gateway</html>"), NullLogger.Instance));
        Assert.Equal(502, ex.HttpStatus);
    }

    [Fact]
    public void NonJson_With2xx_RaisesAudDSerializationException()
    {
        Assert.Throws<AudDSerializationException>(() =>
            ResponseDecoder.DecodeOrThrow(NonJson(200, "not-json"), NullLogger.Instance));
    }

    [Fact]
    public void Code51_WithResult_LogsWarningAndPassesThrough()
    {
        var capturing = new CapturingLogger();
        var json = """{"status":"error","error":{"error_code":51,"error_message":"deprecated"},"result":{"timecode":"00:01"}}""";
        var body = ResponseDecoder.DecodeOrThrow(Env(json), capturing);
        // Now status is rewritten as success; result is preserved.
        Assert.True(body.TryGetProperty("status", out var s));
        Assert.Equal("success", s.GetString());
        Assert.True(body.TryGetProperty("result", out _));
        Assert.Single(capturing.Warnings);
        Assert.Contains("deprecated", capturing.Warnings[0]);
    }

    [Fact]
    public void Code51_NoResult_RaisesInvalidRequest()
    {
        var json = """{"status":"error","error":{"error_code":51,"error_message":"deprecated"},"result":null}""";
        Assert.Throws<AudDInvalidRequestException>(() =>
            ResponseDecoder.DecodeOrThrow(Env(json), NullLogger.Instance));
    }

    [Fact]
    public void UnexpectedStatus_RaisesAudDServerException()
    {
        var json = """{"status":"unknown"}""";
        Assert.Throws<AudDServerException>(() =>
            ResponseDecoder.DecodeOrThrow(Env(json), NullLogger.Instance));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
