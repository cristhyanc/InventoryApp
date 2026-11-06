using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One decision a Super Admin made about one business's Nayax SQS sender identity (issue #1101):
/// append-only evidence of who recorded, changed, switched or removed what.
///
/// <para><strong>It is written in the same transaction as the row it is about</strong>, in the same
/// <c>SaveChanges</c>, so there is no moment at which a database says a business's sales may be
/// attributed to an IAM user with no record of who said so, or holds a record of a decision that was
/// rolled back. That is the whole reason it is a row here rather than only a log event: a log is
/// delivered by a pipeline nobody can roll back, and "whose sales are these?" is exactly the kind of
/// question that has to be answerable years later.</para>
///
/// <para><strong>Tenant-owned, with nothing added</strong>, like
/// <see cref="BusinessNayaxSqsSender"/> itself and like <see cref="BusinessPlanGrantAudit"/> beside
/// it: a decision is a fact about one business, so <c>AppDbContext</c> gives it the business key, the
/// index, the query filter and the ownership stamp. Nothing updates or deletes a row - there is no
/// edit path and no reversal path, because an audit trail that could be rewritten is not one, and
/// the write lease refuses an update or a delete of this table even while it is open.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> The business's name, anybody's
/// display name or email address, and no AWS access key, secret or ARN - the sender id is the only
/// AWS value in this application at all. The actor's Entra <c>(tid, oid)</c> pair is an identifier
/// rather than personal detail and is what an investigation needs.</para>
/// </summary>
public class BusinessNayaxSqsSenderAudit : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The sender id the row carried before this decision, or <c>null</c> when it carried none -
    /// which is both a first recording and a change made after a removal.
    /// </summary>
    public string? FromAwsSenderId { get; set; }

    /// <summary>
    /// The sender id it carries now, or <c>null</c> when the decision removed it. Both sides are
    /// recorded rather than only the new one, so a trail answers "what was it before?" without
    /// depending on the row before it being readable.
    /// </summary>
    public string? ToAwsSenderId { get; set; }

    /// <summary>Whether the mapping was in force before this decision.</summary>
    public required bool FromIsEnabled { get; set; }

    /// <summary>Whether it is in force now.</summary>
    public required bool ToIsEnabled { get; set; }

    /// <summary>The revision the write produced, so a trail can be read in the order it happened.</summary>
    public required int Revision { get; set; }

    /// <summary>The acting platform administrator's Entra <c>tid</c>.</summary>
    public required string ActorDirectoryTenantId { get; set; }

    /// <summary>Their Entra <c>oid</c>.</summary>
    public required string ActorObjectId { get; set; }

    /// <summary>When the decision was committed, from the application clock.</summary>
    public DateTime OccurredAtUtc { get; set; }
}
