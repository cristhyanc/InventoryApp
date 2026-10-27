using Inventory.Application.Costing;
using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMultiMachinePickListRefillStore"/> (issue #897). Like
/// every other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the
/// persistence models it reads, and it applies no rule of its own.
///
/// <para><strong>It writes the same two tables as <see cref="EfPickListRefillStore"/></strong>, with
/// each allocation's machine on its own line (<see cref="PickListRefillLine.MachineId"/>, additive)
/// and no machine on the batch row. One table means one per-business idempotency key namespace, which
/// is deliberate: a key is a submission, and a submission is either a single-machine refill or a
/// multi-machine batch, never both. The write lock and the duplicate-key translation are therefore
/// <see cref="PickListRefillWriteLock"/>'s, shared with that adapter, so the two endpoints serialise
/// against each other exactly as each does against its own kind.</para>
///
/// <para><strong>Each allocation becomes the movement the single-product refill already records.</strong>
/// <see cref="ApplyAsync"/> goes through the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296), the same pair
/// <see cref="EfStockAdjustmentStore"/>, <see cref="EfInventoryCountAdjustmentStore"/> and
/// <see cref="EfPickListRefillStore"/> use, so a multi-machine batch inherits exactly the established
/// <c>MachineRefill</c> costing and audit behaviour: storage quantity down, costing quantity,
/// inventory value and COGS untouched, and no second machine-stock movement. It costs nothing itself
/// and it does not commit - the caller owns the transaction.</para>
/// </summary>
public sealed class EfMultiMachinePickListRefillStore : IMultiMachinePickListRefillStore
{
    private readonly AppDbContext _db;
    private readonly IRecordInventoryMovement _recordMovement;
    private readonly IRebuildProductCost _rebuild;

    public EfMultiMachinePickListRefillStore(
        AppDbContext db, IRecordInventoryMovement recordMovement, IRebuildProductCost rebuild)
    {
        _db = db;
        _recordMovement = recordMovement;
        _rebuild = rebuild;
    }

    /// <inheritdoc />
    public Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken) =>
        PickListRefillWriteLock.BeginAsync(_db, cancellationToken);

    /// <inheritdoc />
    public async Task<MultiMachinePickListRefillSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        // The AppDbContext tenant query filter scopes this read; there is no per-call BusinessId
        // clause to go stale, and one business's key therefore never finds another's refill.
        var refill = await _db.PickListRefills
            .AsNoTracking()
            .Where(row => row.IdempotencyKey == idempotencyKey)
            .Select(row => new
            {
                row.IdempotencyKey,
                row.RequestFingerprint,
                row.MachineId,
                row.Notes,
                row.RecordedAtUtc,
                Lines = row.Lines
                    .OrderBy(line => line.Id)
                    .Select(line => new
                    {
                        line.MachineId,
                        line.ProductId,
                        line.QuantityTaken,
                        line.StockAdjustmentId,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (refill is null)
        {
            return null;
        }

        // A multi-machine batch is the shape this contract recorded: no machine on the batch row, at
        // least one line, and a machine on every line. Anything else under this key is the other
        // endpoint's refill, which is reported as unanswerable rather than reshaped into a batch - the
        // fingerprint would refuse it anyway, and a record assembled out of a refill this contract did
        // not record is precisely what "never reinterpreted" rules out.
        if (refill.MachineId is not null
            || refill.Lines.Count == 0
            || refill.Lines.Any(line => line.MachineId is null))
        {
            return new MultiMachinePickListRefillSnapshot(refill.RequestFingerprint, null);
        }

        return new MultiMachinePickListRefillSnapshot(
            refill.RequestFingerprint,
            new MultiMachinePickListRefillRecord(
                refill.IdempotencyKey,
                refill.Notes,
                refill.RecordedAtUtc,
                [.. refill.Lines.Select(line => new MultiMachinePickListRefillRecordedAllocation(
                    line.MachineId!.Value, line.ProductId, line.QuantityTaken, line.StockAdjustmentId))]));
    }

    /// <inheritdoc />
    public async Task<MultiMachinePickListRefillRecord> ApplyAsync(
        MultiMachinePickListRefillClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var refill = new PickListRefill
        {
            IdempotencyKey = claim.IdempotencyKey,
            RequestFingerprint = claim.RequestFingerprint,

            // No machine on the batch: this contract's machines are per allocation, and copying one
            // of them up here would make the batch claim a machine the operator never gave it.
            MachineId = null,
            Notes = claim.Notes,
            RecordedAtUtc = claim.RecordedAtUtc,
        };

        foreach (var allocation in claim.Allocations)
        {
            // Negative by the units taken: StockAdjustment.QuantityChange is signed from storage's
            // point of view throughout the application, so a refill out of storage is negative and
            // the reverse transfer is the same movement positive. The machine recorded on it is this
            // allocation's own, which is what makes the audit trail of a multi-machine run identical
            // to the same run entered one machine at a time.
            var movement = await _recordMovement
                .RecordAsync(
                    new InventoryMovement(
                        allocation.ProductId,
                        -allocation.QuantityTaken,
                        DomainStock.StockAdjustmentReason.MachineRefill,
                        claim.Notes ?? string.Empty,
                        PurchaseUnitCost: null,
                        allocation.MachineId,
                        EatBefore: null,
                        DomainStock.StockAdjustmentSource.Manual),
                    cancellationToken)
                .ConfigureAwait(false);

            // Saved and replayed per allocation, so the next allocation of the same product is costed
            // against the state this one left rather than the state the batch started from.
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _rebuild.RebuildAsync(allocation.ProductId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            refill.Lines.Add(new PickListRefillLine
            {
                MachineId = allocation.MachineId,
                ProductId = allocation.ProductId,
                QuantityTaken = allocation.QuantityTaken,
                StockAdjustmentId = movement.Id,
            });
        }

        _db.PickListRefills.Add(refill);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MultiMachinePickListRefillRecord(
            refill.IdempotencyKey,
            refill.Notes,
            refill.RecordedAtUtc,
            [.. refill.Lines.Select(line => new MultiMachinePickListRefillRecordedAllocation(
                line.MachineId!.Value, line.ProductId, line.QuantityTaken, line.StockAdjustmentId))]);
    }

    /// <inheritdoc />
    public bool IsDuplicateIdempotencyKey(Exception exception) =>
        PickListRefillWriteLock.IsDuplicateIdempotencyKey(exception);
}
