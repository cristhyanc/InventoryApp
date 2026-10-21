using System.Data;
using Inventory.Application.Costing;
using Inventory.Application.PickList;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPickListRefillStore"/> (issue #743). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence models
/// it reads, and it applies no rule of its own.
///
/// <para><strong>It asks SQLite for <c>BEGIN IMMEDIATE</c>, like
/// <see cref="EfBusinessMembershipWriteStore"/> (issue #522) and
/// <see cref="EfNayaxConnectionSaveTransactions"/> (issue #537), and for the same reason.</strong>
/// A deferred transaction - the default - takes its write lock at the first write, which is
/// <em>after</em> the two reads this refill is decided on: the idempotency key claim, and the
/// storage quantity each row is checked against. Two pickers would both read "enough stock" and
/// both deduct, and two copies of one submission would both read "no refill under this key" and
/// both record one. <c>BEGIN IMMEDIATE</c> takes the write lock when the transaction opens, so the
/// second caller waits (within <c>Default Timeout</c>) and then reads the first one's committed
/// state - which is what makes the loser's refusal the deliberate not-enough-stock sentence, or its
/// retry the original result, rather than a lock error.</para>
///
/// <para>A non-SQLite relational provider falls back to its default transaction - still one
/// transaction around the claim, the checks and the writes - and the EF InMemory provider some
/// tests use has no transaction at all, exactly as the other stores in this folder treat it.
/// Neither fallback is a supported production configuration (docs/architecture.md § SQLite
/// operating assumptions).</para>
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
    /// <summary>
    /// The columns of the per-business unique index on the key, as SQLite names them in a
    /// constraint-violation message. Both have to appear, so a violation of any other constraint on
    /// this table is left untranslated.
    /// </summary>
    private static readonly string[] IdempotencyKeyIndexColumns =
        ["PickListRefills.BusinessId", "PickListRefills.IdempotencyKey"];

    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

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
    public async Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            // Nothing to begin, or already inside somebody else's transaction: that caller owns the
            // commit, and starting a second one here would either fail or commit half of it.
            return new WriteTransaction(null, null, null, null);
        }

        var connection = _db.Database.GetDbConnection();

        // Only closed if this transaction opened it: a test that shares one open connection across
        // contexts, and a request that already has one, must keep theirs.
        DatabaseFacade? openedBy = null;
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            openedBy = _db.Database;
        }

        if (connection is not SqliteConnection sqlite)
        {
            return new WriteTransaction(
                null,
                null,
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false),
                openedBy);
        }

        // deferred: false is BEGIN IMMEDIATE. Serializable is the only isolation level SQLite has,
        // and the driver offers the deferred choice on the synchronous overload only - which costs
        // nothing here, since beginning a transaction is one small statement on an open connection.
        var immediate = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        // Enlisted so the movements, the rebuilds and the batch row are written inside this
        // transaction rather than each opening one of their own - which SQLite would refuse, and
        // which would put the deductions outside the lock the stock was checked under.
        var enlisted = await _db.Database
            .UseTransactionAsync(immediate, cancellationToken)
            .ConfigureAwait(false);

        return new WriteTransaction(immediate, enlisted, null, openedBy);
    }

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
    public bool IsDuplicateIdempotencyKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite || sqlite.SqliteErrorCode != SqliteConstraintViolation)
            {
                continue;
            }

            if (IdempotencyKeyIndexColumns.All(column =>
                sqlite.Message.Contains(column, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The transaction handed back to <see cref="RecordPickListRefill"/>. It holds the provider
    /// transaction it owns and, separately, the EF wrapper that enlisted it: EF's wrapper is not
    /// owned (it came from <c>UseTransaction</c>), so disposing it only detaches the transaction
    /// from the context, and the rollback-on-dispose a refused refill relies on has to come from
    /// disposing the provider transaction itself.
    /// </summary>
    private sealed class WriteTransaction(
        SqliteTransaction? immediate,
        IDbContextTransaction? enlisted,
        IDbContextTransaction? owned,
        DatabaseFacade? openedConnection) : IPickListRefillWriteTransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (immediate is not null)
            {
                await immediate.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (enlisted is not null)
            {
                await enlisted.DisposeAsync().ConfigureAwait(false);
            }

            if (immediate is not null)
            {
                // Rolls back when CommitAsync was not reached, which is how a refused row discards
                // every movement the batch had already staged.
                await immediate.DisposeAsync().ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }

            if (openedConnection is not null)
            {
                await openedConnection.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}
