namespace InventoryApi.DTOs;

/// <summary>
/// One allocation a client posts to <c>POST /api/pick-list/multi-machine-refills</c> (issue #897): a
/// local machine of the caller's business (<c>Machines.Id</c>, issue #741 - never a provider machine
/// number), a product of its own catalogue, and the whole number of units taken out of storage for
/// that pair.
///
/// <para><see cref="MachineId"/> is nullable so that an allocation which omits it is refused by
/// <c>Inventory.Domain.Stock.MultiMachinePickListRefillPolicy</c> with a message on its own line,
/// rather than bound to <c>0</c> and reported as an unknown machine. A refill for no machine at all is
/// still recorded by <c>POST /api/pick-list/refills</c>, which is unchanged.</para>
///
/// <para><see cref="QuantityTaken"/> is an <c>int</c>, so a fractional quantity is refused by model
/// binding before any rule runs; the rule that it must be greater than zero is that same policy's.</para>
/// </summary>
public sealed record MultiMachineRefillAllocationRequest(long? MachineId, long ProductId, int QuantityTaken);

/// <summary>
/// The multi-machine refill batch a client posts to <c>POST /api/pick-list/multi-machine-refills</c>
/// (issue #897): one request recording a whole pick run, machine by machine, all or nothing.
///
/// <para><see cref="IdempotencyKey"/> is the client's own opaque string for this submission. A new
/// submission gets a new key and a retry of the same submission reuses its key, which is what makes
/// the request safe to repeat: the same key with the same allocations - in any order - is answered
/// with the original result, and the same key with different allocations is refused with
/// <c>409 Conflict</c>. The key namespace is shared with the single-machine endpoint, so a key spent
/// there is refused here rather than replayed.</para>
///
/// <para>No owning business and no request-level machine: the business is resolved from the caller's
/// membership (AGENTS.md § Tenant ownership and data isolation), and every machine is named by the
/// allocation it belongs to.</para>
///
/// <para><see cref="Allocations"/> is a list of nullable allocations because that is what the wire can
/// carry: <c>"allocations": [null]</c> deserializes to a missing element whatever the element type
/// declares, so the contract admits it and the policy refuses it on its own line with a <c>400</c> -
/// rather than a non-nullable declaration producing a <c>500</c> on the first read of a property.</para>
/// </summary>
public sealed record MultiMachinePickListRefillRequest(
    string? IdempotencyKey,
    string? Notes,
    IReadOnlyList<MultiMachineRefillAllocationRequest?>? Allocations);

/// <summary>
/// One recorded allocation of a multi-machine batch: the machine, the product, the units taken, and
/// the id of the <c>MachineRefill</c> stock movement that recorded it - the same movement
/// <c>GET /api/products/{productId}/stock</c> and <c>GET /api/stock-history</c> serve.
/// </summary>
public sealed record MultiMachinePickListRefillRecordedAllocationResponse(
    long MachineId, long ProductId, int QuantityTaken, int StockAdjustmentId);

/// <summary>
/// What <c>POST /api/pick-list/multi-machine-refills</c> answers, for a batch this request recorded
/// and for a retry of one already recorded alike.
///
/// <para><see cref="Replayed"/> is the only difference between the two: <see langword="false"/> when
/// this request recorded the movements, <see langword="true"/> when the key had already recorded them
/// and nothing new was written. Everything else is the recorded batch, so a client that lost the first
/// response needs no second call to find out what happened.</para>
/// </summary>
public sealed record MultiMachinePickListRefillResponse(
    string IdempotencyKey,
    bool Replayed,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<MultiMachinePickListRefillRecordedAllocationResponse> Allocations);
