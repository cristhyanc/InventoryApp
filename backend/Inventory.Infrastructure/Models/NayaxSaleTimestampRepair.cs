using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Where an applied sale timestamp repair's authoritative instant came from (issue #472). The
/// numeric values mirror <c>Inventory.Domain.Nayax.NayaxSaleTimestampEvidenceSource</c>.
/// </summary>
public enum NayaxSaleTimestampEvidenceSource
{
    /// <summary>The live Lynx rolling window, <c>GET /v1/machines/{MachineID}/lastSales</c>.</summary>
    NayaxLastSalesApi = 1,

    /// <summary>An operator-supplied export carrying the <c>AuthorizationDateTimeGMT</c> column.</summary>
    OperatorExport = 2,
}

/// <summary>
/// One append-only record of one stored Nayax sale's authorization instant being repaired
/// (issue #472).
///
/// Two populations of <see cref="NayaxSales.MachineAuthorizationTime"/> values were never the
/// authoritative instant - rows ingested before issue #380, which hold machine-local wall-clock
/// ticks, and rows the live synchronization stored between issues #380 and #471 from an offset-free
/// <c>AuthorizationDateTimeGMT</c> value, which hold an instant shifted by the Sydney host's own UTC
/// offset. Changing such a value moves revenue between business days and recosts historical
/// COGS, so every change is recorded here with what it moved, where the authoritative value came
/// from, which reviewed preview authorised it and which operator confirmed it.
///
/// The row is immutable: there is no update or delete path anywhere in the application. A repair
/// that turns out to be wrong is a historical fact that was recorded, and correcting it afterwards is
/// another explicit, previewed repair - which is also why the audit keeps the previous instant.
/// </summary>
public class NayaxSaleTimestampRepair : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>
    /// The remote Nayax transaction identifier of the repaired sale. An external identity, unique
    /// only within the operator account that issued it, which is why the repair verified it against
    /// the stored sale's machine and settled amount before moving it.
    /// </summary>
    public long TransactionId { get; set; }

    /// <summary>The remote Nayax machine identifier the repaired sale belongs to.</summary>
    public long MachineId { get; set; }

    /// <summary>The UTC instant the sale held before the repair.</summary>
    public DateTime PreviousInstantUtc { get; set; }

    /// <summary>The authoritative UTC instant the repair wrote.</summary>
    public DateTime RepairedInstantUtc { get; set; }

    /// <summary>
    /// The business date the sale reported on before the repair, stored
    /// rather than derived so the movement the operator approved stays readable exactly as applied.
    /// </summary>
    public DateTime PreviousBusinessDate { get; set; }

    /// <summary>The business date the sale reports on after the repair.</summary>
    public DateTime RepairedBusinessDate { get; set; }

    /// <summary>Which supported source the authoritative instant was read from.</summary>
    public NayaxSaleTimestampEvidenceSource EvidenceSource { get; set; }

    /// <summary>
    /// Caller-safe provenance text naming that read - a window read instant or an uploaded file name.
    /// Never a credential, a card number or a filesystem path.
    /// </summary>
    public string EvidenceReference { get; set; } = string.Empty;

    /// <summary>The reviewed preview this repair was confirmed from.</summary>
    public Guid PreviewId { get; set; }

    public DateTime AppliedAt { get; set; }

    /// <summary>
    /// The Entra directory tenant id (<c>tid</c>) of the operator who applied the repair, following
    /// <see cref="BusinessMembership"/>: the validated <c>(tid, oid)</c> pair is this application's
    /// identity for an actor, and no email address or display name is stored, because those are
    /// mutable, reassignable personal data.
    /// </summary>
    public string AppliedByDirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) of the operator who applied the repair.</summary>
    public string AppliedByObjectId { get; set; } = string.Empty;
}

/// <summary>
/// One stored sale timestamp repair preview (issue #472): the server-derived plan, as JSON, that an
/// Apply confirms by naming it.
///
/// The plan is stored rather than handed back to the caller for two reasons. The apply then writes
/// only instants and provenance the server itself decided, so a tampered request can change nothing;
/// and the draft carries the single-use and expiry state that makes confirming a reviewed plan a
/// one-time action. It is tenant-owned, so another business's preview id does not exist for a caller.
/// </summary>
public class NayaxSaleTimestampRepairPreviewDraft : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public Guid Id { get; set; }

    /// <summary>The serialized plan: every examined sale, its decision and its source provenance.</summary>
    public string PlanJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>When the plan was applied, or <c>null</c> while it may still be applied once.</summary>
    public DateTime? AppliedAt { get; set; }
}
