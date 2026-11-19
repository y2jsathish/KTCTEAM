using Ktc.Application.Options;

namespace Ktc.Tests;

public class EntraIdOptionsTests
{
    [Fact] public void Off_by_default() => Assert.False(new EntraIdOptions().IsConfigured);

    [Fact] public void Needs_tenant_client_and_secret()
    {
        var o = new EntraIdOptions { Enabled = true, TenantId = "t", ClientId = "c" };
        Assert.False(o.IsConfigured);
        o.ClientSecret = "s";
        Assert.True(o.IsConfigured);
        o.Enabled = false;
        Assert.False(o.IsConfigured);
    }
}
