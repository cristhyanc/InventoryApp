using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One product and one supplier a collector run was asked to search (issue #924, parent #817), and
/// what the collector later said about it.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> A piece
/// of work is a product description and a supplier's shelf, which are facts about products and about
/// no business: this row carries no <c>BusinessId</c>, implements no <see cref="IBusinessOwned"/>,
/// has no query filter and takes no ownership stamp. It is declared in
/// <c>BusinessOwnershipCoverageTests.PlatformEntities</c> beside the seven tables of #848, #873, #923
/// and #925, never in <c>DeliberatelyGlobalEntities</c>.</para>
///
/// <para><strong>It exists so a result can be attributed to issued work.</strong> Every row the
/// results endpoint of #937 reports - a newly found pack included - references the work item it
/// answers, which is what makes "did the collector search this, and what happened?" answerable
/// instead of inferred from whether a price turned up. The run reference restricts, so a run cannot
/// be deleted from under the work it issued.</para>
///
/// <para><strong>Exactly one of <see cref="MasterProductId"/> and <see cref="WaitingKey"/> names the
/// product</strong>, which is the shape <see cref="SupplierListing"/> already uses and for the same
/// reason: a search is about one product, and a row naming two or none is work no result could be
/// attributed to. The rule is a check constraint as well as
/// <see cref="PriceCollectorWorkItemPolicy.HasExactlyOneProduct"/>, because a stored row is not
/// something an application-level check can be trusted to have prevented.</para>
///
/// <para><strong>No outcome is a state.</strong> <see cref="Outcome"/> is null while the work has
/// been issued and not reported on; it never means "nothing happened". See
/// <see cref="PriceCollectorWorkItemOutcome"/>.</para>
/// </summary>
public class PriceCollectorWorkItem
{
    /// <summary>
    /// The work item's own key, assigned by the database. This <em>is</em> the work-item id the work
    /// list publishes and a result cites: unique across the whole table, and so unique within its run
    /// without a second per-run number existing to disagree with it.
    /// </summary>
    public long Id { get; set; }

    /// <summary>The run that issued this work. Required, and restricted on delete.</summary>
    public long PriceCollectorRunId { get; set; }

    /// <summary>The run itself.</summary>
    public virtual PriceCollectorRun? PriceCollectorRun { get; set; }

    /// <summary>
    /// The supplier master record to search. A <see cref="SupplierPreset"/>, which issue #931 made the
    /// supplier master for price checks; required, and restricted on delete for the reason a listing's
    /// supplier is.
    /// </summary>
    public int SupplierPresetId { get; set; }

    /// <summary>The supplier master record itself.</summary>
    public virtual SupplierPreset? SupplierPreset { get; set; }

    /// <summary>
    /// The master product being searched for, or <see langword="null"/> when a waiting product is
    /// being searched for by name instead. Restricted on delete, so a master product that work was
    /// issued against cannot be removed from under it.
    /// </summary>
    public long? MasterProductId { get; set; }

    /// <summary>The master product itself, while one is named.</summary>
    public virtual MasterProduct? MasterProduct { get; set; }

    /// <summary>
    /// The waiting key being searched for, or <see langword="null"/> once a master product is named
    /// instead. Plain text that carries no business or product identity of any kind - it is the folded
    /// name and unit label of <c>PriceCatalogueWaitingKey</c>, which is what several businesses'
    /// spellings of one product fold to.
    /// </summary>
    public string? WaitingKey { get; set; }

    /// <summary>When the work was issued, from the application clock.</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>
    /// What the collector reported, or <see langword="null"/> while it has not reported. Written by
    /// the results endpoint of #937; this issue only ever writes the absence.
    /// </summary>
    public PriceCollectorWorkItemOutcome? Outcome { get; set; }

    /// <summary>
    /// When the outcome was recorded, or <see langword="null"/> while there is none. Kept beside the
    /// outcome rather than derived from the run's end instant: a run reports its work items as it goes,
    /// and a work item's own instant is what says when this search actually finished.
    /// </summary>
    public DateTime? OutcomeAtUtc { get; set; }

    /// <summary>
    /// Why the search failed, for an outcome of <see cref="PriceCollectorWorkItemOutcome.Failed"/>
    /// and for no other (issue #937). <see langword="null"/> everywhere else, which the database
    /// does not check only because doing so would hard-code the outcome enum's numbers into
    /// migration history - <c>PriceCollectorWorkItemPolicy.RequireReportableAnswer</c> is the rule.
    ///
    /// <para>A closed vocabulary rather than a sentence, because the reason is what decides whose
    /// problem a failing search is: a collector defect, a provider's quota, or a slow site.</para>
    /// </summary>
    public PriceCollectorSearchFailureReason? FailureReason { get; set; }

    /// <summary>
    /// How many distinct supplier product ids the collector declared it had submitted for this
    /// search, or <see langword="null"/> while the search is unanswered (issue #937).
    ///
    /// <para>It is the collector's own total, stored as submitted, and the backend accepts the
    /// outcome only while it equals the rows it has actually received for this work item in this run
    /// - so a batch lost in transit leaves the search unanswered rather than silently finished. It
    /// is kept afterwards because a second, identical outcome call has to be recognisable as
    /// identical, and the total is part of what the collector said.</para>
    /// </summary>
    public int? SubmittedRowCount { get; set; }
}
