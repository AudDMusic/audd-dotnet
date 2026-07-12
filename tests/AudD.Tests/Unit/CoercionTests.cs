using System.Globalization;
using System.Text.Json;
using AudD;
using AudD.Internal;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Coercion policy for the lenient parsing layer. A wrong-typed scalar field is
/// coerced toward its target CLR shape when convertible (numeric string → number,
/// number → string, float → truncated int, number/whitelisted-string → bool),
/// degrading to null only when the value is not convertible. Locks the exact
/// renderings (invariant culture) and exercises both the <see cref="TolerantParser"/>
/// primitives and the <see cref="StreamCallbackMatchJsonConverter"/> path.
/// </summary>
public class CoercionTests
{
    private static JsonElement E(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- CoerceToString ----

    [Theory]
    [InlineData("85", "85")]        // int → raw token text
    [InlineData("8.5", "8.5")]      // float → raw token text, invariant (no comma)
    [InlineData("-3", "-3")]
    [InlineData("true", "true")]    // bool → lowercase literal
    [InlineData("false", "false")]
    [InlineData("\"hi\"", "hi")]    // string passes through
    public void CoerceToString_Convertible_ProducesExactRendering(string json, string expected)
    {
        Assert.Equal(expected, TolerantParser.CoerceToString(E(json)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":1}")]
    public void CoerceToString_NonScalar_IsNull(string json)
    {
        Assert.Null(TolerantParser.CoerceToString(E(json)));
    }

    [Fact]
    public void CoerceToString_Float_UsesInvariantCulture_NeverLocaleComma()
    {
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("8.5", TolerantParser.CoerceToString(E("8.5")));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = prev;
        }
    }

    // ---- CoerceToInt64 ----

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("\"42\"", 42L)]       // numeric string
    [InlineData("\"7.9\"", 7L)]       // float-formatted string → truncate
    [InlineData("\" 8.5 \"", 8L)]     // trimmed then parsed
    [InlineData("7.9", 7L)]           // float → truncate toward zero
    [InlineData("-7.9", -7L)]         // truncate toward zero (not floor)
    [InlineData("true", 1L)]
    [InlineData("false", 0L)]
    public void CoerceToInt64_Convertible(string json, long expected)
    {
        Assert.Equal(expected, TolerantParser.CoerceToInt64(E(json)));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"85abc\"")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("{}")]
    public void CoerceToInt64_NotConvertible_IsNull(string json)
    {
        Assert.Null(TolerantParser.CoerceToInt64(E(json)));
    }

    // ---- CoerceToDouble ----

    [Theory]
    [InlineData("42", 42d)]
    [InlineData("\"8.5\"", 8.5d)]
    [InlineData("\" 8.5 \"", 8.5d)]
    public void CoerceToDouble_Convertible(string json, double expected)
    {
        Assert.Equal(expected, TolerantParser.CoerceToDouble(E(json)));
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("{}")]
    public void CoerceToDouble_NotConvertible_IsNull(string json)
    {
        Assert.Null(TolerantParser.CoerceToDouble(E(json)));
    }

    // ---- CoerceToBool ----

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("42", true)]     // number != 0 → true
    [InlineData("-1", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void CoerceToBool_NumberAndLiterals(string json, bool expected)
    {
        Assert.Equal(expected, TolerantParser.CoerceToBool(E(json)));
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\" 1 \"", true)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"on\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"0\"", false)]
    [InlineData("\"no\"", false)]
    [InlineData("\"off\"", false)]
    [InlineData("\"\"", false)]
    public void CoerceToBool_StringWhitelist_BothDirections(string json, bool expected)
    {
        Assert.Equal(expected, TolerantParser.CoerceToBool(E(json)));
    }

    [Theory]
    [InlineData("\"maybe\"")]      // unrecognized string → null (NOT true)
    [InlineData("\"2\"")]
    [InlineData("null")]
    [InlineData("[true]")]
    [InlineData("{}")]
    public void CoerceToBool_Unrecognized_IsNull(string json)
    {
        Assert.Null(TolerantParser.CoerceToBool(E(json)));
    }

    // ---- StreamCallbackMatchJsonConverter path ----

    private static StreamCallbackMatch ParseMatch(string resultJson)
    {
        var body = $$"""{"result":{{resultJson}},"timestamp":0}""";
        var ev = AudDHelpers.ParseCallback(body);
        return Assert.IsType<CallbackEvent.Match>(ev).Value;
    }

    [Fact]
    public void Converter_StringScore_CoercesToNumber()
    {
        var m = ParseMatch("""{"radio_id":9,"results":[{"artist":"A","title":"T","score":"85"}]}""");
        Assert.Equal(85, m.Song.Score);
        Assert.Equal("A", m.Song.Artist);
    }

    [Fact]
    public void Converter_NumberArtist_CoercesToString()
    {
        var m = ParseMatch("""{"radio_id":9,"results":[{"artist":123,"title":"T"}]}""");
        Assert.Equal("123", m.Song.Artist);
    }

    [Fact]
    public void Converter_FloatArtist_CoercesToRawTokenString()
    {
        var m = ParseMatch("""{"radio_id":9,"results":[{"artist":8.5,"title":"T"}]}""");
        Assert.Equal("8.5", m.Song.Artist);
    }

    [Fact]
    public void Converter_NonNumericScore_DegradesToNull()
    {
        var m = ParseMatch("""{"radio_id":9,"results":[{"artist":"A","title":"T","score":"abc"}]}""");
        Assert.Null(m.Song.Score);
        Assert.Equal("A", m.Song.Artist);
    }

    [Fact]
    public void Converter_FloatScore_TruncatesTowardZero()
    {
        var m = ParseMatch("""{"radio_id":9,"results":[{"artist":"A","title":"T","score":7.9}]}""");
        Assert.Equal(7, m.Song.Score);
    }

    [Fact]
    public void Converter_StringRadioId_CoercesToNumber()
    {
        var m = ParseMatch("""{"radio_id":"77","results":[{"artist":"A","title":"T"}]}""");
        Assert.Equal(77L, m.RadioId);
    }

    [Fact]
    public void Converter_NumberTimestamp_CoercesToString()
    {
        var m = ParseMatch("""{"radio_id":9,"timestamp":123,"results":[{"artist":"A","title":"T"}]}""");
        Assert.Equal("123", m.Timestamp);
    }

    [Fact]
    public void Converter_EmptyStringRadioId_DegradesToNull()
    {
        var m = ParseMatch("""{"radio_id":"","results":[{"artist":"A","title":"T"}]}""");
        Assert.Null(m.RadioId);
    }

    [Fact]
    public void Converter_NonNumericPlayLength_DegradesToNull()
    {
        var m = ParseMatch("""{"radio_id":9,"play_length":"abc","results":[{"artist":"A","title":"T"}]}""");
        Assert.Null(m.PlayLength);
        Assert.Equal(9L, m.RadioId);
    }
}
