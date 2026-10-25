using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One batch refill recorded from the pick list (issue #743, parent #329 child H1): the idempotency
/// key the client sent, the fingerprint of the request that key recorded, and the rows it produced.
///
/// <para><strong>It is not a second stock ledger.</strong> The movements are the facts, and they are
/// ordinary <see cref="StockAdjustment"/> rows with reason <c>MachineRefill</c> and source
/// <c>Manual</c> - exactly what the single-product refill action records - so nothing in costing,
/// the machine-stock ledger or Sync Restock has to learn about this table. What this table adds is
/// the request's identity: which submission recorded which movements, so a retry can be answered
/// with the result the original was answered with instead of deducting the shelf twice.</para>
///
/// <para>Append-only. Nothing updates or deletes a row here: a recorded refill is what the operator
/// did, and a correction is its own movement.</para>
/// </summary>
public class PickListRefill : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>
    /// The key the client chose for this submission, unique per business: the pair
    /// <c>(BusinessId, IdempotencyKey)</c> carries a unique index, because two businesses may
    /// legitimately pick the same string (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// <c>Inventory.Domain.Stock.PickListRefillFingerprint</c> of the request this key recorded, as
    /// lowercase hexadecimal SHA-256. It is what tells a retry of this submission from a different
    /// submission under a spent key; it is recomputed on the server for every request and is never
    /// accepted from one.
    /// </summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// The machine the whole pick was for, or <c>null</c> when the refill named none: a local machine,
    /// <c>Machines.Id</c> (issue #741), never the provider's machine number. The pair
    /// <c>(BusinessId, MachineId)</c> is a foreign key on the machines' <c>(BusinessId, Id)</c> key,
    /// restricted on delete, the same as the movement's own machine reference.
    ///
    /// <para>It is also <c>null</c> for a multi-machine batch (issue #897), which attributes each of
    /// its lines to its own machine through <see cref="PickListRefillLine.MachineId"/> instead. What
    /// tells the two apart is never this column: it is the stored
    /// <see cref="RequestFingerprint"/>, whose rendering carries the contract that produced it, so a
    /// key spent on one kind of refill can only ever be refused as reused by the other.</para>
    /// </summary>
    public long? MachineId { get; set; }

    /// <summary>The operator's optional note, copied onto every movement the batch records.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The instant the server recorded the batch, from the <c>IClock</c> port, stored as UTC. The
    /// request carries no instant.
    /// </summary>
    public DateTime RecordedAtUtc { get; set; }

    public virtual ICollection<PickListRefillLine> Lines { get; set; } = [];
}

/// <summary>
/// One product of one recorded batch refill (issue #743): the product, the units the operator said
/// they took, and the <see cref="StockAdjustment"/> that recorded the movement.
///
/// <para>The submitted quantity is stored rather than derived from the movement because this row is
/// the recorded <em>answer</em> to an idempotent request: a retry must be told what the original
/// request was told, whatever has happened to the catalogue since. The movement remains the
/// authoritative inventory fact - nothing reads this table to decide a quantity, a cost or a
/// machine-stock level.</para>
/// </summary>
public class PickListRefillLine : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>The batch this row belongs to; cascaded on delete, like every other owned child row.</summary>
    public long RefillId { get; set; }

    [JsonIgnore]
    public virtual PickListRefill? Refill { get; set; }

    public long ProductId { get; set; }

    /// <summary>
    /// The machine this one allocation was for, when the batch attributed its lines to machines
    /// individually (issue #897), or <c>null</c> when the batch carried one machine - or none - on
    /// <see cref="PickListRefill.MachineId"/> instead. A local machine, <c>Machines.Id</c> (issue
    /// #741), never the provider's machine number; the pair <c>(BusinessId, MachineId)</c> is a
    /// foreign key on the machines' <c>(BusinessId, Id)</c> key, restricted on delete, the same as
    /// the batch's own machine reference and the movement's.
    ///
    /// <para>Additive, and <c>null</c> on every row recorded before it existed: a single-machine
    /// refill's machine is on the batch and is not copied down here, because the two endpoints record
    /// what their own caller submitted and nothing reinterprets one as the other.</para>
    /// </summary>
    public long? MachineId { get; set; }

    /// <summary>The units taken out of storage, always positive: the movement itself is negative.</summary>
    public int QuantityTaken { get; set; }

    /// <summary>
    /// The <see cref="StockAdjustment"/> this row recorded. A plain reference rather than a foreign
    /// key, deliberately: a movement is cascade-deleted with its product, and a restricting key here
    /// would make a product that was once picked undeletable - a rule about deleting a product that
    /// this table has no business inventing.
    /// </summary>
    public int StockAdjustmentId { get; set; }
}
