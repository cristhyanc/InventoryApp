using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// Which work one call of <c>GET /api/price-collector/work</c> asks for (issue #1108).
///
/// <para>Numbered from 1, so a stored or bound <c>0</c> is never read as the first declared value.</para>
/// </summary>
public enum PriceCollectorWorkSchedule
{
    /// <summary>
    /// Every searchable supplier paired with every tracked product the rotation reaches, exactly as
    /// the work list behaved before issue #1108 - and the default, so a manual run is unchanged.
    ///
    /// <para><strong>It deliberately bypasses the whole due rule</strong>, the in-flight protection
    /// included: a manual full run may issue, and pay for, a pair that is already covered or already
    /// being searched. That is the point of it - somebody asking for a full run has decided to
    /// re-search everything - and it is kept on purpose rather than left over.</para>
    /// </summary>
    All = 1,

    /// <summary>
    /// Only the product-and-supplier pairs <see cref="SupplierSearchDuePolicy"/> says are owed a
    /// search. The scheduled workflow of #1110 is what asks for this.
    /// </summary>
    Due = 2,
}

/// <summary>
/// What one collector run is asked to do (issue #924, parent #817): which suppliers are
/// searched, and how much work one run may be handed.
///
/// <para><strong>Every collectable supplier is searched on every run</strong> (owner decision,
/// 1 November 2026): there is no per-supplier cadence. How often a run happens is the workflow's
/// schedule; how old a price may be before it stops being compared is still
/// <see cref="SupplierPriceFreshnessPolicy"/>'s.</para>
///
/// <para><strong>A supplier nothing collects is not due</strong>, however fresh or stale its prices
/// are. A preset with no connector - Costco's state, and a real one - has no route to take, and a
/// preset whose <em>Search prices</em> switch is off is one the platform has stopped paying to
/// search (issue #1077); neither is work. Whether the preset is still <em>suggested</em> to
/// businesses is a separate stored fact and is no part of this decision.</para>
///
/// <para><strong>The response is bounded in every dimension that grows.</strong> A work item is one
/// product and one supplier, so the work grows with the product of the two: a thousand tracked
/// products across nine suppliers is nine thousand searches, which is neither a day's work for the
/// collector nor a response anything should be asked to hold in memory. <see cref="MaxWorkItemsPerRun"/>
/// bounds the work, <see cref="MaxMasterProductsForMatching"/> bounds the matching vocabulary, and
/// <see cref="MaxListingsPerProduct"/> and <see cref="MaxRefusedMasterProductsPerWaitingProduct"/>
/// bound the context carried under each published product - because one product with ten thousand
/// listings would otherwise be an unbounded response that honoured both of the first two limits. A
/// run says when it hit any of them rather than returning a silent prefix: a truncated work list
/// nobody is told about reads as "this is everything there is to search".</para>
///
/// <para><strong>A bounded run continues where the last one stopped.</strong> The products are issued
/// in one deterministic rotation - matched products by master product id, then waiting products by
/// waiting key - and a run starts at the product after the one the most recently issued run stopped
/// at, wrapping around to the start. That is what makes the bound a <em>schedule</em> rather than a
/// cut-off: with a catalogue larger than one run's budget, successive runs issue successive stretches
/// of it and every tracked product is eventually searched, which is the parent issue's requirement.
/// Without it a run would re-issue the same prefix for ever and the tail of the catalogue would never
/// be priced at all. See <see cref="MatchedProductComesAfter"/> and
/// <see cref="WaitingProductComesAfter"/>.</para>
/// </summary>
public static class PriceCollectorWorkListPolicy
{
    /// <summary>
    /// The most work items one run may be issued with.
    ///
    /// <para>Chosen as a bound on the response rather than as a statement about the catalogue: at
    /// nine shipped suppliers it is well over two hundred products a run, which is far more than the
    /// catalogue holds, and it keeps the serialized work list in the low megabytes however large the
    /// catalogue grows. A deployment that outgrows it gets a flagged, deterministic stretch of the
    /// rotation, and the next run is issued the stretch after it, so the remainder arrives on later
    /// runs rather than never.</para>
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

    /// <summary>
    /// The most listings one published product may carry with it - the known listings under a matched
    /// product, and the proposals under a waiting key.
    ///
    /// <para>It is the dimension the work-item limit cannot see. A work item is one product and one
    /// supplier, so two thousand of them bound how many <em>products</em> a run carries and say
    /// nothing about how much travels under each; one product whose catalogue entry has accumulated
    /// ten thousand listings would be an unbounded response that honoured every other limit. Nine
    /// shipped suppliers make a handful of listings the normal state and fifty generous headroom, so
    /// a product beyond it is a catalogue a person needs to look at rather than a run to make
    /// larger.</para>
    ///
    /// <para>A product beyond the bound is still issued its work, with <em>none</em> of its listings
    /// and the omission stated. Those listings are a recognition aid - "you have seen this one
    /// before" - and losing it costs a re-reported listing that the unique index on
    /// <c>(supplier master record, supplier product id)</c> reconciles, never a lost search. Sending
    /// an unflagged prefix of them would instead read as "this product has four listings", which is
    /// the one thing a bound must not say.</para>
    /// </summary>
    public const int MaxListingsPerProduct = 50;

    /// <summary>
    /// The most refused master products one waiting product may carry with it.
    ///
    /// <para>The refusals are a <strong>safeguard</strong> rather than context: they are the pairs a
    /// Super Admin rejected or undid, sent so the matching of #941 never proposes one of them again.
    /// A truncated refusal list would therefore let a refused pair be proposed a second time, which
    /// is why a waiting product whose refusals exceed this bound is published with <em>no</em>
    /// refusals and with matching suppressed for that key on that run: the collector may still search
    /// the name and report what it finds, and it may propose no master product for it. Nothing
    /// silently drops a refusal, and a key with more than a hundred refused master products is a
    /// catalogue a person needs to look at.</para>
    /// </summary>
    public const int MaxRefusedMasterProductsPerWaitingProduct = 100;

    /// <summary>What a caller whose configured work-item budget is not positive is told.</summary>
    public const string NonPositiveBudgetMessage =
        "A price collector run must be allowed at least one work item.";

    /// <summary>
    /// The query value that selects <see cref="PriceCollectorWorkSchedule.All"/>, and the value an
    /// absent one means.
    /// </summary>
    public const string AllScheduleValue = "all";

    /// <summary>The query value that selects <see cref="PriceCollectorWorkSchedule.Due"/>.</summary>
    public const string DueScheduleValue = "due";

    /// <summary>What a caller who asked for a schedule that is neither of those two is told.</summary>
    public static readonly string UnknownScheduleMessage =
        $"A price collector work list's schedule must be '{AllScheduleValue}' or "
            + $"'{DueScheduleValue}'.";

    /// <summary>
    /// Which work a submitted <c>schedule</c> query value asks for: <c>all</c> or nothing at all for
    /// every searchable pair, <c>due</c> for only the pairs that are owed a search.
    ///
    /// <para>An unrecognised value is refused rather than resolved to either one. Reading it as
    /// <c>all</c> would make a typo in the scheduled workflow re-search and re-pay for the whole
    /// catalogue; reading it as <c>due</c> would make a typo in a manual run look like a run that had
    /// nothing to do.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">The value is neither of the two.</exception>
    public static PriceCollectorWorkSchedule RequireSchedule(string? schedule)
    {
        var trimmed = schedule?.Trim();

        if (string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, AllScheduleValue, StringComparison.OrdinalIgnoreCase))
        {
            return PriceCollectorWorkSchedule.All;
        }

        return string.Equals(trimmed, DueScheduleValue, StringComparison.OrdinalIgnoreCase)
            ? PriceCollectorWorkSchedule.Due
            : throw new DomainValidationException(UnknownScheduleMessage);
    }

    /// <summary>
    /// Whether a supplier is searched on a run: every supplier whose <em>Search prices</em> switch is
    /// on and that something collects, on every run.
    ///
    /// <para><strong>Whether the platform still suggests the supplier is not part of this
    /// decision</strong> (owner decision, 3 November 2026, #1077). One flag used to answer two
    /// questions, so the only way to stop paying for a supplier's searches - the paid Kmart and BIG W
    /// routes, for example - was to stop offering that supplier to businesses as well. They are now
    /// two stored facts: <c>SupplierPreset.SearchPrices</c> is read here, and
    /// <c>SupplierPreset.IsEnabled</c> decides only whether the preset is suggested, which is the
    /// provisioning copy's and the "Available to add" list's question and never this one's.</para>
    ///
    /// <para><strong>There is no cadence here</strong> (owner decision, 1 November 2026, #929): a run
    /// searches every product at every collectable supplier, whenever it was last checked. A
    /// supplier-level "last checked" let one partial run mark a whole supplier as checked, so the
    /// products that run never reached waited a full cycle. How often a supplier is searched is now
    /// the schedule's decision, and a price's freshness is still
    /// <see cref="SupplierPriceFreshnessPolicy"/>'s.</para>
    /// </summary>
    /// <param name="searchPrices">Whether this supplier's prices are searched for at all.</param>
    /// <param name="hasConnector">Whether anything collects this supplier's prices at all.</param>
    public static bool IsSupplierDue(bool searchPrices, bool hasConnector) =>
        searchPrices && hasConnector;

    /// <summary>
    /// The work-item budget a run is issued with: the configured one, bounded by
    /// <see cref="MaxWorkItemsPerRun"/> so no setting can widen the response past the reviewed limit.
    /// </summary>
    /// <exception cref="DomainValidationException">The configured budget is not positive.</exception>
    public static int BudgetFor(int configuredBudget) =>
        configuredBudget > 0
            ? Math.Min(configuredBudget, MaxWorkItemsPerRun)
            : throw new DomainValidationException(NonPositiveBudgetMessage);

    /// <summary>
    /// Whether a <strong>matched</strong> product comes after the point the most recently issued run
    /// stopped at, which is where the next run starts.
    ///
    /// <para>The rotation is matched products by master product id and then waiting products by
    /// waiting key, so: with no previous run every matched product is after the cursor; with a cursor
    /// among the matched products those with a higher id are; and with a cursor among the waiting
    /// products none is, because the waiting products are the end of the rotation and a matched
    /// product is reached by wrapping around rather than by coming after.</para>
    ///
    /// <para>The comparison is on the stored id rather than on a position, so a product added to or
    /// removed from the catalogue between two runs cannot send the rotation back to the start: the
    /// next run still begins at the first id above the one it stopped at.</para>
    /// </summary>
    /// <param name="masterProductId">The candidate product's own key.</param>
    /// <param name="cursorMasterProductId">
    /// The master product the previous run stopped at, or <see langword="null"/> when it stopped at a
    /// waiting product or there was no previous run.
    /// </param>
    /// <param name="cursorWaitingKey">
    /// The waiting key the previous run stopped at, on the same terms.
    /// </param>
    public static bool MatchedProductComesAfter(
        long masterProductId,
        long? cursorMasterProductId,
        string? cursorWaitingKey) =>
        cursorWaitingKey is null
            && (cursorMasterProductId is not { } cursor || masterProductId > cursor);

    /// <summary>
    /// Whether a <strong>waiting</strong> product comes after that same point: every one of them does
    /// unless the previous run stopped among them, in which case those with an ordinally greater
    /// waiting key do.
    ///
    /// <para>Ordinal, because the waiting key is a fold rather than a word: it is compared the way the
    /// #923 port orders it, so the rotation and the read agree about what "after" means whatever
    /// collation a database would have chosen.</para>
    ///
    /// <para>It takes no master product id, unlike <see cref="MatchedProductComesAfter"/>: a cursor
    /// among the matched products is exactly the case where <paramref name="cursorWaitingKey"/> is
    /// absent, and every waiting product comes after it.</para>
    /// </summary>
    /// <param name="waitingKey">The candidate product's own folded key.</param>
    /// <param name="cursorWaitingKey">
    /// The waiting key the previous run stopped at, or <see langword="null"/> when it stopped at a
    /// matched product or there was no previous run.
    /// </param>
    public static bool WaitingProductComesAfter(string waitingKey, string? cursorWaitingKey)
    {
        ArgumentNullException.ThrowIfNull(waitingKey);

        return cursorWaitingKey is not { } cursor
            || string.CompareOrdinal(waitingKey, cursor) > 0;
    }
}
