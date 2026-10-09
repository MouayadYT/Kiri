using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>The External Web and Image Search permission now also covers the Integration Finder's lookup (PROJECT_SPEC section 3.4 item 5, step 106), and it says so.</summary>
public sealed class ExternalSearchPermissionTextTests
{
    [Fact]
    public void ThePermissionSaysItAlsoLooksUpIntegrationsAndWhatIsSent()
    {
        var definition = PermissionCatalog.Get(PermissionCapability.ExternalSearch);

        // A few words, but it still says that it looks up integrations and that Local Only decides whether anything leaves this PC.
        Assert.Contains("integrations", definition.Summary, StringComparison.Ordinal);
        Assert.Contains("Local Only", definition.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ItStillLeavesThisPcAndStartsOff()
    {
        Assert.True(PermissionCatalog.Get(PermissionCapability.ExternalSearch).LeavesThisPc);
        Assert.False(new PermissionSettings().ExternalSearch);
        Assert.True(new PrivacySettings().LocalOnly);
    }
}
