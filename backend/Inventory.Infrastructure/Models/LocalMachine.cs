using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// A vending machine this business operates, as a row this application owns (issue #740, parent #329
/// child G1). It maps the <c>Machines</c> table.
///
/// <para><strong>Until now a machine was never persisted at all</strong>: every machine list was a
/// read of the live Nayax fleet, and every stored machine reference - a sale's
/// <c>MachineID</c>, a refill's <c>MachineId</c>, a machine count's - was Nayax's own number. A
/// business with no sales provider therefore had no machines, which is what this table fixes: a
/// machine can be created, renamed, deactivated and reactivated with no provider in the picture at
/// all.</para>
///
/// <para><strong><see cref="Id"/> is this application's own key, assigned by the database</strong>
/// (the rule issue #735 established for products and categories, decision A4). Nothing assigns it,
/// and a local id that happens to equal a Nayax machine id is a coincidence.</para>
///
/// <para><strong><see cref="ExternalMachineId"/> is the provider's opaque text id for this
/// machine</strong>, or <c>null</c> when the provider does not name it - the machine half of
/// <c>Product.ExternalProductId</c> (issue #759), with the same rules: issued by
/// <c>Business.SalesProvider</c>, unique per business among the machines that have one, written for
/// Nayax only through <c>Inventory.Domain.Providers.NayaxExternalId</c>, and never read as a local
/// id. It is <strong>never a value a request chooses</strong>: the <c>AddMachines</c> backfill wrote
/// it for the machines that existed, and since issue #742 the provider machine review is the one
/// thing that writes it afterwards - from a provider fleet read the server made itself, either
/// automatically or on an operator's explicit link-or-create decision. It is deliberately off the
/// wire, like the product and category columns.</para>
///
/// <para>Issue #740 repointed nothing at this table. Since issue #780 (G2a)
/// <c>OperatingExpense.MachineId</c> holds <see cref="Id"/>, and since issue #741 (the stock half of
/// G2) <c>StockAdjustment.MachineId</c> and <c>MachineStockCount.MachineId</c> do too, each with
/// <c>(BusinessId, MachineId)</c> a foreign key on this row's <c>(BusinessId, Id)</c> key. The
/// provider's own data - a sale's, a stock event's, a timestamp repair's and an inventory-cost
/// transition's machine - still holds the provider's number, and a reader that needs both resolves
/// one to the other through <see cref="ExternalMachineId"/>.</para>
///
/// <para>The name <c>LocalMachine</c> rather than <c>Machine</c> is not a second concept: the
/// retired response shape <see cref="Machine"/> still occupies that type name as the reference value
/// of the machine wire-contract test, and removing it belongs to issue #153's legacy-structure
/// removal.</para>
/// </summary>
public class LocalMachine : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>This application's own key, assigned by the database. Never a provider's id.</summary>
    public long Id { get; set; }

    /// <summary>What the operator calls the machine. Required, and otherwise unconstrained.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the machine is in service. A machine with history is deactivated rather than deleted,
    /// because stored sales, refills, counts and expenses refer to it and a machine that stops
    /// trading is history rather than a mistake. A machine nothing refers to and no provider names
    /// <em>can</em> be deleted since issue #896 - that is the machine added by mistake, and
    /// <c>Inventory.Domain.Machines.MachineDeletionPolicy</c> is the one rule that tells the two
    /// apart.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The business's sales provider's own identifier for this machine, or <c>null</c> when it names
    /// none. Opaque text, compared exactly, and never written through the machine API.
    /// </summary>
    [JsonIgnore]
    public string? ExternalMachineId { get; set; }

    /// <summary>
    /// When the last fully successful <em>Sync Restock</em> operation for this machine
    /// <strong>started</strong>, or <c>null</c> when none is recorded (issue #891).
    ///
    /// <para>The start instant rather than the completion instant, because a fetch can only have
    /// accounted for alerts that already existed when it began. It is written by
    /// <c>SyncMachineStockFromNayax</c> alone, only after that workflow's alert read, import and
    /// preview construction have all succeeded and only when the machine has nothing left to sync
    /// (issue #1018), and only ever forwards - see
    /// <c>IMachineRestockSyncCoverageStore.TryRecordSuccessfulSyncAsync</c> for the monotonic
    /// conditional write.</para>
    ///
    /// <para><strong><c>null</c> is "no recorded sync", not "never synced".</strong> Every machine
    /// that existed when this column was added starts null, including machines whose restocks were
    /// being synced perfectly well; nothing backfills it, and nothing infers it from an event's
    /// <c>CreatedAt</c>/<c>ProcessedAt</c>, from a latest-sales or catalogue sync, or from a machine
    /// stock count. It is also never a claim of complete historical coverage: Nayax's last-alerts
    /// read is an upstream rolling window, so this says when the workflow last succeeded and nothing
    /// more.</para>
    ///
    /// <para>Integration state, like <see cref="ExternalMachineId"/>: deliberately off the machine
    /// wire contract, and never a value a request chooses.</para>
    /// </summary>
    [JsonIgnore]
    public DateTime? LastSuccessfulRestockSyncStartedAtUtc { get; set; }

    /// <summary>
    /// The <c>BusinessNayaxConnection.CredentialRevision</c> that successful fetch authenticated
    /// under, or <c>null</c> when none is recorded (issue #891).
    ///
    /// <para>A credential save supersedes everything the previous token proved, so coverage stamped
    /// with an older revision is read as no recorded sync at all - that is what stops an operation
    /// that completed under replaced credentials from marking the new ones fresh. The pair
    /// <c>(revision, started instant)</c> is also the ordering the conditional write advances
    /// along.</para>
    /// </summary>
    [JsonIgnore]
    public int? LastSuccessfulRestockSyncCredentialRevision { get; set; }
}
