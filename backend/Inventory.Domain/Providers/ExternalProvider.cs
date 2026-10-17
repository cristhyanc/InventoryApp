namespace Inventory.Domain.Providers;

/// <summary>
/// The external system a local product or category is linked to (issue #732, parent #329 decision
/// P1).
///
/// <para>It names the issuer of an <c>ExternalId</c>, because an external id only means something
/// together with whoever issued it: the same digits are a different product in a different
/// provider's catalogue. That is why the provider is part of both uniqueness rules on a link table -
/// one link per <c>(business, provider, external id)</c>, and one link per <c>(business, provider,
/// local row)</c> - rather than implied by the table it is stored in.</para>
///
/// <para>Nayax is the only member today and the only integration this repository implements. The
/// enum exists anyway, because the alternative is a column that means "Nayax" by convention; a
/// second provider then has to change the meaning of stored rows instead of adding a member.</para>
///
/// <para>Numbered from 1, deliberately. Zero is the value an unwritten integer column and a
/// default-constructed entity both carry, and no provider should be reachable by forgetting to set
/// one: an undeclared stored value is not a provider, exactly as an undeclared role is not a role
/// (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public enum ExternalProvider
{
    /// <summary>The Nayax Lynx operator catalogue (<c>NayaxProductID</c>, <c>ProductGroupID</c>).</summary>
    Nayax = 1,
}
