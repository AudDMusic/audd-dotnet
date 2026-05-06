using System.Text.Json;
using AudD;
using Xunit;

namespace AudD.Tests.Unit;

public class ErrorsTests
{
    [Fact]
    public void AudDErrorMap_KnownCodes_MapToExpectedTypes()
    {
        Assert.IsType<AudDAuthenticationException>(AudDErrorMap.FactoryFor(900)(900, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDAuthenticationException>(AudDErrorMap.FactoryFor(901)(901, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDAuthenticationException>(AudDErrorMap.FactoryFor(903)(903, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDQuotaException>(AudDErrorMap.FactoryFor(902)(902, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDSubscriptionException>(AudDErrorMap.FactoryFor(904)(904, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDSubscriptionException>(AudDErrorMap.FactoryFor(905)(905, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDInvalidRequestException>(AudDErrorMap.FactoryFor(50)(50, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDInvalidRequestException>(AudDErrorMap.FactoryFor(51)(51, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDInvalidAudioException>(AudDErrorMap.FactoryFor(300)(300, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDInvalidAudioException>(AudDErrorMap.FactoryFor(400)(400, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDInvalidAudioException>(AudDErrorMap.FactoryFor(500)(500, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDRateLimitException>(AudDErrorMap.FactoryFor(611)(611, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDStreamLimitException>(AudDErrorMap.FactoryFor(610)(610, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDNotReleasedException>(AudDErrorMap.FactoryFor(907)(907, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDBlockedException>(AudDErrorMap.FactoryFor(19)(19, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDBlockedException>(AudDErrorMap.FactoryFor(31337)(31337, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDNeedsUpdateException>(AudDErrorMap.FactoryFor(20)(20, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDServerException>(AudDErrorMap.FactoryFor(100)(100, "x", 200, null, null, null, null, default));
        Assert.IsType<AudDServerException>(AudDErrorMap.FactoryFor(1000)(1000, "x", 200, null, null, null, null, default));
    }

    [Fact]
    public void AudDErrorMap_UnknownCode_FallsBackToServerException()
    {
        var e = AudDErrorMap.FactoryFor(99999)(99999, "weird", 200, null, null, null, null, default);
        Assert.IsType<AudDServerException>(e);
    }

    [Fact]
    public void AudDErrorMap_Register_OverridesMapping()
    {
        AudDErrorMap.Register(99998, (c, m, h, rid, rp, rm, bm, rr) =>
            new AudDBlockedException(c, m, h, rid, rp, rm, bm, rr));
        var e = AudDErrorMap.FactoryFor(99998)(99998, "x", 200, null, null, null, null, default);
        Assert.IsType<AudDBlockedException>(e);
    }

    [Fact]
    public void AudDCustomCatalogAccess_OverridesMessage_PreservesOriginal()
    {
        var e = new AudDCustomCatalogAccessException(904, "original server text", 200);
        Assert.Equal("original server text", e.OriginalServerMessage);
        Assert.Contains("Adding songs to your custom catalog", e.Message);
        Assert.Contains("[Server message: original server text]", e.Message);
    }

    [Fact]
    public void ErrorRaiser_Builds_AuthenticationException_For900()
    {
        var json = """
        {
          "status":"error",
          "error":{"error_code":900,"error_message":"bad token"},
          "request_params":{"api_token":"d***a"},
          "request_api_method":"recognize"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var exc = ErrorRaiser.BuildFromErrorBody(doc.RootElement, httpStatus: 200, requestId: "req-x");
        var ae = Assert.IsType<AudDAuthenticationException>(exc);
        Assert.Equal(900, ae.ErrorCode);
        Assert.Equal("bad token", ae.ServerMessage);
        Assert.Equal("req-x", ae.RequestId);
        Assert.Equal("recognize", ae.RequestMethod);
        Assert.True(ae.RequestedParams.ContainsKey("api_token"));
    }

    [Fact]
    public void ErrorRaiser_HandlesRequestedParamsAlternateSpelling()
    {
        var json = """
        {
          "status":"error",
          "error":{"error_code":904,"error_message":"only paid"},
          "requested_params":{"limit":"1"}
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var exc = ErrorRaiser.BuildFromErrorBody(doc.RootElement, httpStatus: 200, requestId: null);
        Assert.IsType<AudDSubscriptionException>(exc);
        Assert.True(exc.RequestedParams.ContainsKey("limit"));
    }

    [Fact]
    public void ErrorRaiser_CustomCatalogContext_For904_RaisesOverride()
    {
        var json = """
        {
          "status":"error",
          "error":{"error_code":904,"error_message":"only paid"}
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var exc = ErrorRaiser.BuildFromErrorBody(doc.RootElement, httpStatus: 200, requestId: null, customCatalogContext: true);
        Assert.IsType<AudDCustomCatalogAccessException>(exc);
    }

    [Fact]
    public void ErrorRaiser_ExtractsBrandedMessage()
    {
        var json = """
        {
          "status":"error",
          "error":{"error_code":31337,"error_message":"blocked"},
          "result":{"artist":"Sorry","title":"your IP was banned"}
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var exc = ErrorRaiser.BuildFromErrorBody(doc.RootElement, httpStatus: 200, requestId: null);
        Assert.NotNull(exc.BrandedMessage);
        Assert.Contains("Sorry", exc.BrandedMessage!);
        Assert.Contains("your IP was banned", exc.BrandedMessage!);
    }
}
