using Inventory.Application.Costing;
using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPickListRefillStore"/> (issue #743). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence models
/// it reads, and it applies no rule of its own.
///
/// <para>The write lock it takes, and the duplicate-key translation it answers with, are
/// <see cref="PickListRefillWriteLock"/>'s: <c>BEGIN IMMEDIATE</c> on SQLite, held before the key
/// claim and the storage read rather than at the first write. The multi-machine batch of issue #897
/// shares both, because it writes the same two tables under the same per-business key namespace.</para>
///
/// <para><strong>Each row becomes the movement the single-product refill already records.</strong>
/// <see cref="ApplyAsync"/> goes through the Application <see cref="IRecordInventoryMovement"/> and
/// <see cref="IRebuildProductCost"/> use cases (issue #296), the same pair
/// <see cref="EfStockAdjustmentStore"/> and <see cref="EfInventoryCountAdjustmentStore"/> use, so a
/// batch refill inherits exactly the established <c>MachineRefill</c> costing and audit behaviour:
/// storage quantity down, costing quantity, inventory value and COGS untouched. It costs nothing
/// itself and it does not commit - the caller owns the transaction.</para>
/// </summary>
public sealed class EfPickListRefillStore : IPickListRefillStore
{
    private readonly AppDbContext _db;
    private readonly IRecordInventoryMovement _recordMovement;
    private readonly IRebuildProductCost _rebuild;

    public EfPickListRefillStore(
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
    public async Task<PickListRefillSnapshot?> FindByIdempotencyKeyAsync(
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
                    .Select(line => new { line.ProductId, line.QuantityTaken, line.StockAdjustmentId })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (refill is null)
        {
            return null;
        }

        return new PickListRefillSnapshot(
            refill.RequestFingerprint,
            new PickListRefillRecord(
                refill.IdempotencyKey,
                refill.MachineId,
                refill.Notes,
                refill.RecordedAtUtc,
                [.. refill.Lines.Select(line => new PickListRefillRecordedRow(
                    line.ProductId, line.QuantityTaken, line.StockAdjustmentId))]));
    }

    /// <inheritdoc />
    public async Task<PickListRefillRecord> ApplyAsync(
        PickListRefillClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var refill = new PickListRefill
        {
            IdempotencyKey = claim.IdempotencyKey,
            RequestFingerprint = claim.RequestFingerprint,
            MachineId = claim.MachineId,
            Notes = claim.Notes,
            RecordedAtUtc = claim.RecordedAtUtc,
        };

        foreach (var row in claim.Rows)
        {
            // Negative by the units taken: StockAdjustment.QuantityChange is signed from storage's
            // point of view throughout the application, so a refill out of storage is negative and
            // the reverse transfer is the same movement positive.
            var movement = await _recordMovement
                .RecordAsync(
                    new InventoryMovement(
                        row.ProductId,
                        -row.QuantityTaken,
                        DomainStock.StockAdjustmentReason.MachineRefill,
                        claim.Notes ?? string.Empty,
                        PurchaseUnitCost: null,
                        claim.MachineId,
                        EatBefore: null,
                        DomainStock.StockAdjustmentSource.Manual),
                    cancellationToken)
                .ConfigureAwait(false);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _rebuild.RebuildAsync(row.ProductId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            refill.Lines.Add(new PickListRefillLine
            {
                ProductId = row.ProductId,
                QuantityTaken = row.QuantityTaken,
                StockAdjustmentId = movement.Id,
            });
        }

        _db.PickListRefills.Add(refill);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PickListRefillRecord(
            refill.IdempotencyKey,
            refill.MachineId,
            refill.Notes,
            refill.RecordedAtUtc,
            [.. refill.Lines.Select(line => new PickListRefillRecordedRow(
                line.ProductId, line.QuantityTaken, line.StockAdjustmentId))]);
    }

    /// <inheritdoc />
    public bool IsDuplicateIdempotencyKey(Exception exception) =>
        PickListRefillWriteLock.IsDuplicateIdempotencyKey(exception);
}
