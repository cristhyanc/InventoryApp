using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One product whose historical inventory cost still has to be replayed, and the earliest instant the
/// replay has to start from (issue #1103).
///
/// <para><strong>Why it exists.</strong> A sale is stored and its costing replay is a separate,
/// longer step afterwards. Before this table, what had to be replayed lived in a dictionary for the
/// length of one call: if the replay failed, the sale stayed stored and the work was simply forgotten
/// - the next import found the transaction already present, reported no affected products, and that
/// product's costing position stayed stale until its next sale. A queue reader that deleted a message
/// on "already stored" would have lost that replay for good. This row is the durable memory that
/// makes the replay recoverable: it is written in the <em>same transaction</em> as the sale, and it is
/// removed in the same transaction as the rebuilt costs.</para>
///
/// <para><strong>One row per product, and the instant only ever moves earlier.</strong> A second sale
/// of the same product does not add a row; it lowers <see cref="FromInstantUtc"/> if it is earlier,
/// because a replay from the earlier instant covers the later one and a replay from the later one
/// does not cover the earlier. Moving it later could silently skip a sale's effect on cost.</para>
///
/// <para><strong><see cref="Revision"/> is what says the work changed.</strong> Because the instant
/// only moves earlier, a sale recorded at or after the instant the row already holds leaves every
/// other column exactly as it was - so the instant cannot be read as a version of the work, and a
/// replay already in flight would acknowledge work it never saw. The revision moves on
/// <em>every</em> enqueue, and it is what the acknowledgement compares.</para>
///
/// <para><strong>It records nothing financial.</strong> No quantity, no cost, no value and no sale:
/// it is a note that says "replay this product from here". Deleting a row changes no stored cost, and
/// a row that is lost would only mean a replay is not retried - never a wrong number.</para>
///
/// <para><strong>Tenant-owned, with nothing added</strong>, like every other table about one
/// business's own data: <c>AppDbContext</c> gives it the business key, its index, the central query
/// filter and the ownership stamp. It deliberately carries no foreign key to
/// <c>Products</c>: a pending replay is transient work, and a restricted key would make deleting a
/// product depend on whether a background replay had caught up - a rule nobody asked for. The product
/// is this business's own by the ownership key it is read under.</para>
/// </summary>
public class PendingInventoryCostRebuild : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The local product whose cost history has to be replayed - <c>Products.Id</c>.</summary>
    public long ProductId { get; set; }

    /// <summary>
    /// The earliest instant still to be replayed from: the authorization instant of the earliest sale
    /// whose effect on this product's cost has not been applied yet.
    /// </summary>
    public DateTime FromInstantUtc { get; set; }

    /// <summary>
    /// The version of the work this row describes: <c>1</c> when it is first recorded, and one higher
    /// every time a sale adds work to it - whether or not that moves <see cref="FromInstantUtc"/>.
    ///
    /// <para>It is the whole of what a replay's acknowledgement compares. The instant cannot do that
    /// job: it only ever moves <em>earlier</em>, so a sale at or after the instant the row holds
    /// changes nothing visible, and a replay that had already read its costing inputs would delete
    /// that sale's recorded work while saving figures computed without it. A revision that moves on
    /// every enqueue is what makes "this is still the work I replayed" answerable at all.</para>
    ///
    /// <para>Recording a failed attempt deliberately does <em>not</em> move it: an attempt is
    /// evidence about a replay, not new work, and invalidating another drain's claim for it would
    /// discard a replay that is still correct.</para>
    /// </summary>
    public long Revision { get; set; }

    /// <summary>When this row was first recorded. It is not moved by a later sale.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// How many times the replay has been attempted and not succeeded. It is operational evidence
    /// about a replay that keeps failing, not a retry budget: nothing stops retrying because of it.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>When the last attempt was made, or <see langword="null"/> before the first.</summary>
    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>
    /// Why the last attempt failed, as a short code.
    ///
    /// <para>It is a <em>code</em> and never the data-quality sentence: a failure message can name a
    /// product, a purchase or a figure, and this column is read by an operational view rather than by
    /// the person the real message is written for. The full message still reaches the import or sync
    /// that raised it.</para>
    /// </summary>
    public string? LastFailureCode { get; set; }
}
