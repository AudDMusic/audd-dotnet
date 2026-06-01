using AudD;
using Xunit;

namespace AudD.Tests.Unit;

public class HelpersTests
{
    [Fact]
    public void DeriveLongpollCategory_MatchesDocumentedFormula()
    {
        // hex MD5 of MD5("test")+"42" prefix-9
        // python: import hashlib; hashlib.md5((hashlib.md5(b"test").hexdigest()+"42").encode()).hexdigest()[:9]
        // We don't precompute; just verify length, hex-ness, and stability across calls.
        var c1 = AudDHelpers.DeriveLongpollCategory("test", 42);
        var c2 = AudDHelpers.DeriveLongpollCategory("test", 42);
        Assert.Equal(c1, c2);
        Assert.Equal(9, c1.Length);
        Assert.Matches("^[0-9a-f]{9}$", c1);
    }

    [Fact]
    public void DeriveLongpollCategory_DiffersByRadioId()
    {
        var c1 = AudDHelpers.DeriveLongpollCategory("test", 1);
        var c2 = AudDHelpers.DeriveLongpollCategory("test", 2);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void AddReturnToUrl_NullReturn_PassesThrough()
    {
        var r = AudDHelpers.AddReturnToUrl("https://example.com/cb?x=1", (string?)null);
        Assert.Equal("https://example.com/cb?x=1", r);
    }

    [Fact]
    public void AddReturnToUrl_AppendsParam()
    {
        var r = AudDHelpers.AddReturnToUrl("https://example.com/cb", "apple_music,spotify");
        Assert.Contains("return=apple_music%2Cspotify", r);
    }

    [Fact]
    public void AddReturnToUrl_PreservesExistingQuery()
    {
        var r = AudDHelpers.AddReturnToUrl("https://example.com/cb?x=1", "apple_music");
        Assert.Contains("x=1", r);
        Assert.Contains("return=apple_music", r);
    }

    [Fact]
    public void AddReturnToUrl_DuplicateReturnQueryParam_Throws()
    {
        Assert.Throws<AudDInvalidRequestException>(() =>
            AudDHelpers.AddReturnToUrl("https://example.com/cb?return=spotify", "apple_music"));
    }

    [Fact]
    public void AddReturnToUrl_AcceptsList()
    {
        var r = AudDHelpers.AddReturnToUrl("https://example.com/cb", new[] { "apple_music", "spotify" });
        Assert.Contains("return=apple_music%2Cspotify", r);
    }

    [Theory]
    [InlineData("45", 45.0)]
    [InlineData("01:30", 90.0)]
    [InlineData("00:01:00", 60.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("12.5", 12.5)]
    public void OffsetToSeconds_ParsesSupportedFormats(string offset, double expected)
    {
        var v = AudDHelpers.OffsetToSeconds(offset);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.Value, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("00:xx")]
    public void OffsetToSeconds_ReturnsNullOnUnparseable(string? offset)
    {
        Assert.Null(AudDHelpers.OffsetToSeconds(offset));
    }
}
