namespace Inventory.Domain.Stock;

/// <summary>
/// One allocation of a multi-machine refill batch as the operator submitted it (issue #897, the
/// backend half of the multi-machine manual pick): the local machine the units are for, the product
/// they are, and the whole number of units taken out of storage for that pair.
///
/// <para><see cref="MachineId"/> is nullable because that is what the wire can carry, not because an
/// allocation without a machine is valid: a batch exists to attribute each line to the machine it was
/// walked to, so <see cref="MultiMachinePickListRefillPolicy"/> refuses an allocation that names
/// none, and a refill for no machine at all keeps using the single-batch endpoint
/// (<c>POST /api/pick-list/refills</c>) unchanged.</para>
///
/// <para>The quantity is <em>taken</em>, always positive, exactly as on
/// <see cref="PickListRefillRequestRow"/>. The movement it becomes is negative, because
/// <c>StockAdjustment.QuantityChange</c> is signed from storage's point of view throughout the
/// application.</para>
/// </summary>
public sealed record MultiMachineRefillAllocation(long? MachineId, long ProductId, int QuantityTaken);

/// <summary>
/// One allocation that has passed <see cref="MultiMachinePickListRefillPolicy.ValidateRequestShape"/>:
/// the same three values, with the machine no longer in question. It is a separate record rather than
/// a nullable field carried further so that nothing downstream - the canonical fingerprint, the claim,
/// the adapter - has to decide what an allocation with no machine would mean.
/// </summary>
public sealed record MultiMachineRefillLine(long MachineId, long ProductId, int QuantityTaken);

/// <summary>
/// The part of a multi-machine batch request a refusal is about. A Domain vocabulary rather than a
/// field name, like <see cref="PickListRefillRequestPart"/>: this layer states <em>what</em> was
/// wrong and the API boundary alone decides how a client addresses it.
/// </summary>
public enum MultiMachineRefillRequestPart
{
    /// <summary>The submitted idempotency key.</summary>
    IdempotencyKey,

    /// <summary>The optional free-text note.</summary>
    Notes,

    /// <summary>The allocation list as a whole - its presence and its size, never one allocation's content.</summary>
    Allocations,
}

/// <summary>One refusal about the request itself.</summary>
public sealed record MultiMachineRefillRequestRefusal(MultiMachineRefillRequestPart Part, string Message);

/// <summary>
/// One refusal about one submitted allocation, identified by its position in the request the operator
/// sent together with the machine and product it named - which is what makes a refusal about a
/// <em>combination</em> of allocations (a product whose allocations together exceed storage) readable
/// on each of the lines that combined.
///
/// <para><see cref="MachineId"/> and <see cref="ProductId"/> are <see langword="null"/> for an
/// allocation that is not there at all, and <see cref="MachineId"/> is <see langword="null"/> for one
/// that named no machine, because reporting either would be this refusal inventing it.</para>
/// </summary>
public sealed record MultiMachineRefillAllocationRefusal(
    int AllocationIndex, long? MachineId, long? ProductId, string Message);

/// <summary>
/// Everything wrong with one multi-machine batch request. An empty rejection is the only thing that
/// lets the batch proceed: any refusal at all, on the request or on a single allocation, refuses the
/// whole batch, because a batch that records some of its machines is the failure mode the endpoint
/// exists to remove.
/// </summary>
public sealed record MultiMachineRefillRejection(
    IReadOnlyList<MultiMachineRefillRequestRefusal> Request,
    IReadOnlyList<MultiMachineRefillAllocationRefusal> Allocations)
{
    /// <summary>Nothing was refused, which is the only state the batch may proceed from.</summary>
    public bool IsEmpty => Request.Count == 0 && Allocations.Count == 0;
}
