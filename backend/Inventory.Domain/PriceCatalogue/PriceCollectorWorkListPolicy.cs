using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What one collector run is asked to do (issue #924, parent #817): which suppliers are due to be
/// searched, and how much work one run may be handed.
///
/// <para><strong>A supplier's cadence is read from its own record, never from its name.</strong>
/// <c>SupplierPreset.PriceCheckFreshnessHours</c> is the one stored cadence, and
/// <see cref="SupplierPriceFreshnessPolicy"/> owns what it means for a <em>price</em>; this policy
/// reads the same number for the other half of the question - when the next check is due - by
/// inverting <see cref="SupplierPriceFreshnessPolicy.LimitForCheckInterval"/>. That is deliberately
/// one number rather than two: a deployment that records "BIG W is checked twice a week" must not
/// have to record it again in a second column that could disagree with the first.</para>
///
/// <para><strong>A supplier nothing collects is not due</strong>, however fresh or stale its prices
/// are. A preset with no connector - Costco's state, and a real one - has no route to take, and a
/// disabled preset is one the platform has stopped suggesting; neither is work.</para>
///
/// <para><strong>The response is bounded, and the bound is on work items.</strong> A work item is one
/// product and one supplier, so the payload grows with the product of the two: a thousand tracked
/// products across nine suppliers is nine thousand searches, which is neither a day's work for the
/// collector nor a response anything should be asked to hold in memory. The run is issued with the
/// first <see cref="MaxWorkItemsPerRun"/> items in a deterministic order and says that it stopped,
/// rather than silently returning a prefix - a truncated work list nobody is told about would read
/// as "this is everything there is to search".</para>
/// </summary>
public static class PriceCollectorWorkListPolicy
{
    /// <summary>
    /// The most work items one run may be issued with.
    ///
    /// <para>Chosen as a bound on the response rather than as a statement about the catalogue: at
    /// nine shipped suppliers it is well over two hundred products a run, which is far more than the
    /// catalogue holds, and it keeps the serialized work list in the low megabytes however large the
    /// catalogue grows. A deployment that outgrows it gets a flagged, deterministic prefix and the
    /// rest on the next run, which is the behaviour an unbounded payload cannot offer at all.</para>
    /// </summary>
    public const int MaxWorkItemsPerRun = 2000;

    /// <summary>
    /// The most master products one run's matching list may carry (issue #941's input).
    ///
    /// <para>It is a separate bound because the matching list is not work: it is the vocabulary a
    /// waiting product is matched against, it is sent only while something is waiting, and it grows
    /// with the whole catalogue rather than with this run. Bounding it keeps the response finite in
    /// the one dimension the work-item limit does not cover.</para>
    /// </summary>
    public const int MaxMasterProductsForMatching = 5000;

    /// <summary>What a caller whose configured work-item budget is not positive is told.</summary>
    public const string NonPositiveBudgetMessage =
        "A price collector run must be allowed at least one work item.";

    /// <summary>
    /// The gap between two checks of a supplier whose stored freshness limit is
    /// <paramref name="freshnessLimitHours"/>: the limit less
    /// <see cref="SupplierPriceFreshnessPolicy.MarginHours"/>, which is exactly the derivation
    /// <see cref="SupplierPriceFreshnessPolicy.LimitForCheckInterval"/> made in the other direction.
    ///
    /// <para>A limit at or below the margin would invert to a non-positive interval, which is a
    /// supplier that is always due; that is the answer this returns rather than an exception, because
    /// "check it every time" is a coherent cadence and refusing a stored row at read time would take
    /// the whole work list down over one supplier's configuration.</para>
    /// </summary>
    public static int CheckIntervalForLimit(int freshnessLimitHours) =>
        Math.Max(freshnessLimitHours - SupplierPriceFreshnessPolicy.MarginHours, 0);

    /// <summary>
    /// Whether a supplier is due to be searched on a run starting at <paramref name="asOfUtc"/>.
    ///
    /// <para>A supplier that has never been successfully checked is due - that is every supplier the
    /// first time the collector runs - and one that has is due once its own check interval has
    /// elapsed since that check. A clock that disagrees with itself, leaving the last check in the
    /// future, leaves the supplier not due, for the reason an observation from the future is treated
    /// as fresh: the alternative is to re-search something that was just searched.</para>
    /// </summary>
    /// <param name="isEnabled">Whether the platform still suggests this supplier.</param>
    /// <param name="hasConnector">Whether anything collects this supplier's prices at all.</param>
    /// <param name="lastCheckedAtUtc">
    /// When this supplier was last checked successfully, or <see langword="null"/> while it never
    /// has been.
    /// </param>
    /// <param name="freshnessLimitHours">The supplier's own stored freshness limit.</param>
    /// <param name="asOfUtc">When the run is starting.</param>
    public static bool IsSupplierDue(
        bool isEnabled,
        bool hasConnector,
        DateTime? lastCheckedAtUtc,
        int freshnessLimitHours,
        DateTime asOfUtc)
    {
        if (!isEnabled || !hasConnector)
        {
            return false;
        }

        if (lastCheckedAtUtc is not { } lastChecked)
        {
            return true;
        }

        return asOfUtc - lastChecked >= TimeSpan.FromHours(CheckIntervalForLimit(freshnessLimitHours));
    }

    /// <summary>
    /// The work-item budget a run is issued with: the configured one, bounded by
    /// <see cref="MaxWorkItemsPerRun"/> so no setting can widen the response past the reviewed limit.
    /// </summary>
    /// <exception cref="DomainValidationException">The configured budget is not positive.</exception>
    public static int BudgetFor(int configuredBudget) =>
        configuredBudget > 0
            ? Math.Min(configuredBudget, MaxWorkItemsPerRun)
            : throw new DomainValidationException(NonPositiveBudgetMessage);
}
