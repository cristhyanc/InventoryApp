using Inventory.Domain.Subscriptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Whose tracked products the shared collector is sent out for (issue #977, child of #965, after
/// #966 and #967).
///
/// <para><strong>One rule for both halves of the issue.</strong> The same feature decides whether a
/// business may read the cheapest-supplier summary at all and whether its tracked products are
/// issued to the collector, so the two cannot drift apart: nothing is searched for at a supplier on
/// behalf of a business that would be refused the answer, and nothing a business is entitled to see
/// goes uncollected. The endpoint declares
/// <see cref="PlanFeature.ObservedSupplierPrices"/> and the work list asks this.</para>
///
/// <para><strong>"At least one entitled business" is not decided here.</strong> A master product is
/// collected once for every business that tracks it and a waiting key once for every spelling that
/// folds to it, so collection is decided for a <em>product description</em> rather than for a
/// business: what the work list does is read the mappings of the businesses this rule entitles, and
/// the deduplication it already performs is what turns "any entitled business tracks it" into one
/// entry. That keeps this a pure per-business question with no notion of how many businesses there
/// are - which is also why a product tracked by a Free <em>and</em> an entitled business is still
/// collected, with nothing added for the case.</para>
///
/// <para><strong>It withholds nothing until enforcement is on.</strong>
/// <see cref="PlanFeaturePolicy.Allows"/> is <see langword="true"/> for every feature on an
/// unenforced entitlement, which is the shipped state, so this rule answers exactly what it does
/// today for every business until a deployment switches the rollout setting on and the
/// grandfathering step has completed (issue #966).</para>
/// </summary>
public static class PriceCollectionEligibilityPolicy
{
    /// <summary>
    /// The feature a business's plan must include before its tracked products are collected, and
    /// before the summary endpoint serves it. A <see langword="const"/> so the one place that names
    /// it is this one.
    /// </summary>
    public const PlanFeature Feature = PlanFeature.ObservedSupplierPrices;

    /// <summary>
    /// Whether <paramref name="entitlement"/> is an entitlement the collector collects for.
    /// </summary>
    /// <param name="entitlement">
    /// What one business may currently use, as <see cref="EffectiveEntitlementPolicy.Decide"/>
    /// decided it.
    /// </param>
    public static bool CollectsFor(EffectiveEntitlement entitlement) =>
        PlanFeaturePolicy.Allows(entitlement, Feature);
}
