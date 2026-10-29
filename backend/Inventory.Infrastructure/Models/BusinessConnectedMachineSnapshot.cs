using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One business's recorded connected-machine count for one UTC day in one Nayax account series
/// (issue #978, child of #965).
///
/// <para><strong>It is billing evidence, and it is append-only.</strong> Nothing in this application
/// updates or deletes a row of this table: the persistence port offers no such method and
/// <c>ConnectedMachineSnapshotEnforcer</c> refuses a modified or deleted row on <c>SaveChanges</c>.
/// A count that turns out to be wrong is a fact that was recorded; a later day is a later row.</para>
///
/// <para><strong>At most one row per business, UTC date and account series</strong>, enforced by a
/// unique index rather than by a check some caller performs: concurrent page loads and the daily job
/// read the same fleet at the same moment, and the first insert to commit is the day's snapshot while
/// the other is discarded without an error.</para>
///
/// <para>Nothing here is a credential or derived from one. <see cref="AccountSeries"/> is an internal
/// counter on the business's own connection row - never the operator id, a token, a key id or a
/// credential revision - and the machine ids are the provider's own identifiers for machines on that
/// account.</para>
/// </summary>
public class BusinessConnectedMachineSnapshot : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>
    /// The UTC day this count is for, at midnight. A UTC day rather than the business's own reporting
    /// day: an allowance is a fact about an account rather than about a trading day, and every
    /// business's snapshots have to be comparable on one calendar.
    /// </summary>
    public DateTime SnapshotDateUtc { get; set; }

    /// <summary>
    /// Which Nayax account this count belongs to, as <c>BusinessNayaxConnection.AccountSeries</c> was
    /// when the count was taken. A business that moves to a different Nayax account starts a new
    /// series, and the previous series' snapshots stay exactly as they are.
    /// </summary>
    public int AccountSeries { get; set; }

    /// <summary>
    /// How many connected machines the account held: the number of ids in
    /// <see cref="ProviderMachineIds"/>, stored beside them so a count can be read without parsing
    /// text.
    /// </summary>
    public int MachineCount { get; set; }

    /// <summary>
    /// The provider machine ids counted, as invariant decimal text, comma separated, ascending -
    /// the canonical form <c>Inventory.Domain.ConnectedMachines.ConnectedMachineFleetRead</c> defines.
    /// They are stored so a count months old can still be explained.
    /// </summary>
    public string ProviderMachineIds { get; set; } = string.Empty;

    /// <summary>When the fleet read this count came from was taken.</summary>
    public DateTime TakenAtUtc { get; set; }

    /// <summary>
    /// Whether the fleet read behind this count accounted for the whole fleet. It is always
    /// <see langword="true"/> on a stored row, because an incomplete read stores nothing at all; it is
    /// recorded rather than assumed so a future decision to keep partial evidence could not be made
    /// invisibly.
    /// </summary>
    public bool ReadWasComplete { get; set; }
}
