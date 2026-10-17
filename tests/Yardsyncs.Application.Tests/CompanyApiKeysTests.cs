using Yardsyncs.Application.Auth;
using Yardsyncs.Domain.Parties;

namespace Yardsyncs.Application.Tests;

public class CompanyApiKeysTests
{
    [Fact]
    public void Keys_are_recognisable_and_apart_from_gate_system_keys()
    {
        var key = CompanyApiKeys.New();
        Assert.True(CompanyApiKeys.LooksLikeKey(key));
        Assert.False(CompanyApiKeys.LooksLikeKey(Yardsyncs.Application.Gate.GateSystemKeys.New()));
        Assert.NotEqual(CompanyApiKeys.New(), key);
        Assert.Equal(64, CompanyApiKeys.Hash(key).Length);
    }

    [Fact]
    public void A_key_needs_a_name_and_a_known_scope()
    {
        Assert.Empty(CompanyApiKeys.Validate(new CompanyApiKeyInput("Our TMS", ApiKeyScope.Read)));
        Assert.NotEmpty(CompanyApiKeys.Validate(new CompanyApiKeyInput(" ")));
        Assert.NotEmpty(CompanyApiKeys.Validate(new CompanyApiKeyInput(new string('x', 61))));
        Assert.NotEmpty(CompanyApiKeys.Validate(new CompanyApiKeyInput("TMS", (ApiKeyScope)9)));
    }

    [Theory]
    [InlineData(true, "GET", true)]
    [InlineData(true, "head", true)]
    [InlineData(true, "POST", false)]
    [InlineData(true, "DELETE", false)]
    [InlineData(false, "POST", true)]
    public void Read_only_keys_only_read(bool readOnly, string method, bool allowed) =>
        Assert.Equal(allowed, CompanyApiKeys.Allows(readOnly, method));
}
