namespace InventoryApi.DTOs;

/// <summary>
/// One row a client posts to <c>POST /api/pick-list/refills</c> (issue #743): a product of its own
/// catalogue and the whole number of units taken out of storage for it.
///
/// <para><see cref="QuantityTaken"/> is an <c>int</c>, so a fractional quantity is refused by model
/// binding before any rule runs; the rule that it must be greater than zero is
/// <c>Inventory.Domain.Stock.PickListRefillPolicy</c>'s.</para>
/// </summary>
public sealed record PickListRefillRowRequest(long ProductId, int QuantityTaken);

/// <summary>
/// The batch refill a client posts to <c>POST /api/pick-list/refills</c> (issue #743, parent #329
/// child H1): one request recording everything a worker took out of storage.
///
/// <para><see cref="IdempotencyKey"/> is the client's own opaque string for this submission. A new
/// submission gets a new key and a retry of the same submission reuses its key, which is what makes
/// the request safe to repeat: the same key with the same rows is answered with the original result,
/// and the same key with different rows is refused with <c>409 Conflict</c>.</para>
///
/// <para><see cref="MachineId"/> is a local machine of the caller's business (<c>Machines.Id</c>,
/// issue #741), never a provider machine number, and it is optional: a business may pick before its
/// machines are recorded. No owning business - it is resolved from the caller's membership
/// (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public sealed record PickListRefillRequest(
    string? IdempotencyKey,
    long? MachineId,
    string? Notes,
    IReadOnlyList<PickListRefillRowRequest>? Rows);

/// <summary>
/// One recorded row of a batch refill: the product, the units taken, and the id of the
/// <c>MachineRefill</c> stock movement that recorded it - the same movement
/// <c>GET /api/products/{productId}/stock</c> and <c>GET /api/stock-history</c> serve.
/// </summary>
public sealed record PickListRefillRecordedRowResponse(long ProductId, int QuantityTaken, int StockAdjustmentId);

/// <summary>
/// What <c>POST /api/pick-list/refills</c> answers, for a refill this request recorded and for a
/// retry of one already recorded alike.
///
/// <para><see cref="Replayed"/> is the only difference between the two: <see langword="false"/> when
/// this request recorded the movements, <see langword="true"/> when the key had already recorded
/// them and nothing new was written. Everything else is the recorded refill, so a client that lost
/// the first response needs no second call to find out what happened.</para>
/// </summary>
public sealed record PickListRefillResponse(
    string IdempotencyKey,
    bool Replayed,
    long? MachineId,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<PickListRefillRecordedRowResponse> Rows);
