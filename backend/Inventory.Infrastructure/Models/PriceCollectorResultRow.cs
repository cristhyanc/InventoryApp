using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One result row a collector submitted, and what this application did with it (issue #937, parent
/// #817).
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> A
/// submitted reading of a supplier's shelf is about no business, so this row carries no
/// <c>BusinessId</c>, has no query filter and takes no ownership stamp. It is declared in
/// <c>BusinessOwnershipCoverageTests.PlatformEntities</c>.</para>
///
/// <para><strong>It exists because an idempotency key has to be stored somewhere, and because three
/// of the four outcomes write nothing else.</strong> The key is the run, the work item and the
/// supplier's own product id (<see cref="PriceCollectorRunId"/>,
/// <see cref="PriceCollectorWorkItemId"/>, <see cref="SupplierProductId"/>) and it is a unique index,
/// so a repeat is answered with the outcome the first attempt got rather than written twice. The
/// supplier product id is <em>in</em> the key deliberately: one search legitimately returns a
/// 24-pack and a 30-pack, and accepting one must never suppress the other.</para>
///
/// <para><strong>It is also the count the outcome call checks against.</strong> "How many distinct
/// supplier product ids has this work item received in this run?" is this table's own row count for
/// that work item, which is why finalising a search split across batches needs no separate
/// bookkeeping (<c>PriceCollectorWorkItemPolicy</c>).</para>
///
/// <para><strong>Nothing is ever deleted or re-decided.</strong> A row records the decision that was
/// made at the time, with the listing and the accepted observation it produced when it produced one;
/// a later run submits its own rows under its own run id.</para>
/// </summary>
public class PriceCollectorResultRow
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The run the result was submitted under - the first part of the idempotency key. Required, and
    /// restricted on delete.
    ///
    /// <para>It is stored beside the work item rather than read through it. The key the issue states
    /// is run, work item and supplier product id, and a column the index can name is what makes that
    /// key the database's rule rather than a join's.</para>
    /// </summary>
    public long PriceCollectorRunId { get; set; }

    /// <summary>The run itself.</summary>
    public virtual PriceCollectorRun? PriceCollectorRun { get; set; }

    /// <summary>The issued work item the row answered. Required, and restricted on delete.</summary>
    public long PriceCollectorWorkItemId { get; set; }

    /// <summary>The work item itself.</summary>
    public virtual PriceCollectorWorkItem? PriceCollectorWorkItem { get; set; }

    /// <summary>
    /// The supplier's own product id the row was about, exactly as
    /// <c>SupplierListingPolicy.NormaliseSupplierProductId</c> stores it on a listing. Opaque text,
    /// compared exactly and never parsed - and recorded even for a row that was refused, because a
    /// refusal is the outcome a repeat of that key must be answered with.
    /// </summary>
    public string SupplierProductId { get; set; } = string.Empty;

    /// <summary>What was done with the row.</summary>
    public PriceCollectorRowOutcome Outcome { get; set; }

    /// <summary>
    /// Why, for every outcome but <see cref="PriceCollectorRowOutcome.Accepted"/>. Stored so a
    /// repeat is answered with the same sentence rather than with a re-derived one.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The listing the row resolved to, or <see langword="null"/> when it resolved to none - a row
    /// refused before any listing was reached. Restricted on delete.
    /// </summary>
    public long? SupplierListingId { get; set; }

    /// <summary>The listing itself, while there is one.</summary>
    public virtual SupplierListing? SupplierListing { get; set; }

    /// <summary>
    /// The accepted observation the row produced, or <see langword="null"/> for every outcome but
    /// <see cref="PriceCollectorRowOutcome.Accepted"/>. Restricted on delete.
    /// </summary>
    public long? SupplierPriceObservationId { get; set; }

    /// <summary>The observation itself, while there is one.</summary>
    public virtual SupplierPriceObservation? SupplierPriceObservation { get; set; }

    /// <summary>When the row was submitted and decided, from the application clock.</summary>
    public DateTime SubmittedAtUtc { get; set; }
}
