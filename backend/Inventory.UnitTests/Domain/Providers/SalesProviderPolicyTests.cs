using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// The one rule about a business's single sales provider (issue #759, parent #329 decision P1 as
/// revised on 18 October 2026).
///
/// It is three lines of Domain, and it decides whether a business that already sells through one
/// provider may have another's credentials stored - which, if it ever said yes, would leave the
/// business's products carrying one system's identifiers while another system's ids were resolved
/// against them. So it is pinned here, away from the adapter that calls it.
/// </summary>
public class SalesProviderPolicyTests
{
    /// <summary>
    /// No provider yet is the state storing credentials is *for*: it is what claims one.
    /// </summary>
    [Fact]
    public void A_business_with_no_sales_provider_may_store_any_providers_credentials()
    {
        Assert.True(SalesProviderPolicy.MayStoreCredentialsFor(null, ExternalProvider.Nayax));
    }

    [Fact]
    public void A_business_may_store_its_own_providers_credentials_again()
    {
        Assert.True(
            SalesProviderPolicy.MayStoreCredentialsFor(ExternalProvider.Nayax, ExternalProvider.Nayax));
    }

    /// <summary>
    /// Nayax is the only provider this application implements, so the other side of the rule is
    /// asserted with the stored value a second provider would have. The rule is about the comparison,
    /// not about how many members the enum happens to declare today - and a stored value this code
    /// does not declare must not be treated as "the same provider" either.
    /// </summary>
    [Fact]
    public void A_business_may_not_store_another_providers_credentials()
    {
        Assert.False(
            SalesProviderPolicy.MayStoreCredentialsFor((ExternalProvider)99, ExternalProvider.Nayax));
    }

    /// <summary>
    /// The refusal names the two providers and nothing else: it reaches the API response and the log,
    /// so no operator id and no part of a token may be in it.
    /// </summary>
    [Fact]
    public void The_refusal_names_both_providers_and_says_the_provider_cannot_change()
    {
        var message = SalesProviderPolicy.RefusalMessage((ExternalProvider)99, ExternalProvider.Nayax);

        Assert.Contains("99", message, StringComparison.Ordinal);
        Assert.Contains("Nayax", message, StringComparison.Ordinal);
        Assert.Contains("one sales provider", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", message, StringComparison.Ordinal);
    }
}
