using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// The append-only trail of stored Nayax sale statuses a queue message changed (issue #1107).
/// Ordinary tenant-owned data: stamped by <c>BusinessOwnershipEnforcer</c>, filtered centrally, and
/// written in the <strong>same</strong> <c>SaveChanges</c> as the status it records, so a changed
/// status and its audit commit together or not at all.
///
/// <para><strong>Exactly one thing writes a row here: applying a refund.</strong> Before issue #1107
/// a stored sale's status was never overwritten by anything, and it still is not by the
/// <c>lastSales</c> synchronization or the uploaded-export import - both of them only ever fill a
/// status that is <c>NULL</c>. The Nayax SQS path is the one writer, it moves a sale from <c>12</c>
/// to <c>62</c> and nothing else, and this row is why that is readable afterwards: which sale, what
/// it was, what it became, which queue message said so, and when.</para>
///
/// <para>The columns are deliberately general - a previous and a new status rather than a refund
/// flag - because what happened is a status change and recording it as one is what lets the trail be
/// read without knowing which rule wrote it. Nothing infers a second kind of change from them.</para>
///
/// <para><strong>No foreign key to the sale</strong>, for the reason
/// <c>NayaxSaleOwnerDecisionAudit</c> declares none: the audit of a financial change must never
/// depend on, or be deleted with, the row it describes.</para>
/// </summary>
public class NayaxSaleStatusChangeAudit : IBusinessOwned
{
    public long Id { get; set; }

    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The local key of the sale whose status changed.</summary>
    public long NayaxSaleId { get; set; }

    /// <summary>The provider's transaction id, recorded so the trail reads without the sale row.</summary>
    public long TransactionId { get; set; }

    /// <summary>
    /// The status the sale held before the change, which is <c>12</c> for every row written today.
    /// Nullable because a stored status can be <c>NULL</c> and an audit must be able to say so
    /// rather than invent a number.
    /// </summary>
    public int? PreviousTransactionStatusId { get; set; }

    /// <summary>The status the sale holds after the change, which is <c>62</c> for every row today.</summary>
    public int NewTransactionStatusId { get; set; }

    /// <summary>
    /// The queue's own id for the message that caused the change. Never a receipt handle and never a
    /// body: a handle is a capability to change a queue message and a body is a card transaction
    /// (AGENTS.md § Nayax integration).
    /// </summary>
    public string SourceMessageId { get; set; } = string.Empty;

    /// <summary>When the reader accepted the message that caused the change.</summary>
    public DateTime OccurredAtUtc { get; set; }
}
