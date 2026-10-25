using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILocalMachineStore"/> (issue #740, parent #329 child
/// G1), beside the <see cref="AppDbContext"/> and the <see cref="LocalMachine"/> entity it reads.
///
/// <para>Each write reads the stored row and writes it in one relational transaction, the same shape
/// <see cref="EfCategoryStore"/> uses: "does this machine exist in my business" and the write that
/// depends on the answer are one operation rather than two statements with a window between
/// them.</para>
///
/// <para>Tenant scoping stays the central <see cref="AppDbContext"/> query filters and
/// <c>BusinessOwnershipEnforcer</c> - there is no <c>BusinessId</c> predicate anywhere here, so a
/// machine of another business is invisible to a read and unreachable to a write, and a created
/// machine is stamped with the caller's business rather than told which one to belong to.</para>
///
/// <para><strong>Nothing here writes <see cref="LocalMachine.ExternalMachineId"/>.</strong>
/// A created machine has none, and the records this adapter returns carry only whether one is
/// present. Whether one is present is also what refuses a rename or a deactivation: a machine the
/// provider names is renamed and retired at the provider, and that refusal is decided against the
/// stored row inside the write's own transaction, so a machine linked between a caller's read and
/// this write is still refused. The column is written by the <c>AddMachines</c> backfill and, since
/// issue #742, by <see cref="EfProviderMachineLinkStore"/> - the provider machine review's own
/// adapter, which is where a link is decided against the stored rows inside its own
/// transaction.</para>
///
/// <para><strong>The delete is the one write that counts other tables</strong> (issue #896). It runs
/// under the same <c>BEGIN IMMEDIATE</c> write lock the provider machine review's apply takes
/// (<see cref="SqliteWriteLockTransaction"/>), counts the four tables that name a machine inside it,
/// and applies <see cref="MachineDeletionPolicy"/>; the restricted foreign keys are the database's own
/// backstop behind that count, and a violation of one becomes the same refusal rather than an
/// unhandled failure. Nothing is cascaded, nulled or reassigned.</para>
/// </summary>
public sealed class EfLocalMachineStore : ILocalMachineStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a foreign-key violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// What SQLite calls a restricted foreign key it refused. Required as well as the code above, so
    /// a violation of any other constraint stays untranslated and surfaces as the failure it is.
    /// </summary>
    private const string ForeignKeyViolationMessage = "FOREIGN KEY constraint failed";

    private readonly AppDbContext _db;

    public EfLocalMachineStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LocalMachineRecord>> ListOrderedByNameAsync(
        CancellationToken cancellationToken) =>
        await WithDeletionEligibilityAsync(
            await Rows(_db.Machines.AsNoTracking().OrderBy(machine => machine.Name).ThenBy(machine => machine.Id))
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<LocalMachineRecord?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        var row = await Rows(_db.Machines.AsNoTracking().Where(machine => machine.Id == id))
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        return (await WithDeletionEligibilityAsync([row], cancellationToken))[0];
    }

    public async Task<LocalMachineWriteAttempt> CreateAsync(
        LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Applied again at the write itself, so the column cannot be written blank by a future
        // caller that forgets - the use case validated the submitted name already.
        if (MachineCataloguePolicy.Validate(fields.Name) is { } invalid)
            return LocalMachineWriteAttempt.Refused(invalid);

        // The key is the database's own (issue #735's rule, decision A4): nothing assigns Id, and
        // nothing writes ExternalMachineId - a machine a person created is this business's own.
        var machine = new LocalMachine
        {
            Name = MachineCataloguePolicy.Normalize(fields.Name!),
            IsActive = true,
        };

        _db.Machines.Add(machine);
        await _db.SaveChangesAsync(cancellationToken);

        return LocalMachineWriteAttempt.Succeeded(machine.Id);
    }

    public async Task<LocalMachineWriteAttempt> RenameAsync(
        long id, LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (MachineCataloguePolicy.Validate(fields.Name) is { } invalid)
            return LocalMachineWriteAttempt.Refused(invalid);

        return await WriteAsync(
            id,
            machine => machine.Name = MachineCataloguePolicy.Normalize(fields.Name!),
            MachineCataloguePolicy.ProviderMachineRenameRefusal,
            cancellationToken);
    }

    /// <summary>
    /// Reactivation is never refused, so a provider machine deactivated before the refusal existed
    /// can be put back in service.
    /// </summary>
    public Task<LocalMachineWriteAttempt> SetActiveAsync(
        long id, bool isActive, CancellationToken cancellationToken) =>
        WriteAsync(
            id,
            machine => machine.IsActive = isActive,
            isActive ? null : MachineCataloguePolicy.ProviderMachineDeactivationRefusal,
            cancellationToken);

    /// <summary>
    /// Reads the machine and applies <paramref name="change"/> to it in one transaction, so the
    /// existence check and the write it depends on cannot be separated by another request deleting
    /// or replacing the row. <paramref name="providerMachineRefusal"/>, when given, refuses the write
    /// for a machine the provider names, worded with the business's provider.
    /// </summary>
    private async Task<LocalMachineWriteAttempt> WriteAsync(
        long id,
        Action<LocalMachine> change,
        Func<ExternalProvider?, string>? providerMachineRefusal,
        CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var machine = await _db.Machines.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (machine is null) return LocalMachineWriteAttempt.NotFound();

        // Returned without committing: nothing was written, and the transaction's disposal ends it.
        if (providerMachineRefusal is not null && machine.ExternalMachineId is not null)
        {
            return LocalMachineWriteAttempt.Refused(providerMachineRefusal(
                await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken)));
        }

        change(machine);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return LocalMachineWriteAttempt.Succeeded(machine.Id);
    }

    /// <summary>
    /// Deletes a machine nothing refers to and no provider names (issue #896).
    ///
    /// <para>The read, the reference count and the delete are one <c>BEGIN IMMEDIATE</c> operation.
    /// Immediate rather than deferred, and the same lock <see cref="EfProviderMachineLinkStore"/>
    /// takes, because the race the restricted foreign keys cannot stop is that review's apply
    /// linking this very row: a deferred transaction would let both read a machine with no provider
    /// id, and SQLite could then only answer one of them with a lock error instead of serialising
    /// them.</para>
    ///
    /// <para>The foreign keys are still the backstop under the count, for the reference that lands in
    /// a window no lock covers - another process, or a provider this application does not serialise
    /// against. SQLite refuses the statement, and that refusal is translated into the same sentence
    /// an operator would have been shown had the count seen it, never into a 500.</para>
    /// </summary>
    public async Task<LocalMachineWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var machine = await _db.Machines.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (machine is null) return LocalMachineWriteAttempt.NotFound();

        // Counted inside the lock, so a provider link, an expense, a count, a movement or a refill
        // recorded since a caller read the machine refuses this delete rather than being destroyed
        // by it.
        var references = await CountReferencesAsync(id, cancellationToken);
        var provider = machine.ExternalMachineId is null
            ? null
            : await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken);

        // Returned without committing: nothing was written, and the transaction's disposal ends it.
        if (MachineDeletionPolicy.Refusal(machine.ExternalMachineId is not null, provider, references)
            is { } refusal)
            return LocalMachineWriteAttempt.Refused(refusal);

        _db.Machines.Remove(machine);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (IsForeignKeyViolation(failure))
        {
            // Nothing was deleted - the statement itself was refused - so the row the caller still
            // sees is intact. Untracked again so the context this request shares does not retry the
            // delete on a later save.
            _db.Entry(machine).State = EntityState.Unchanged;
            return LocalMachineWriteAttempt.Refused(MachineDeletionPolicy.ReferencedWhileDeletingRefusal);
        }

        await transaction.CommitAsync(cancellationToken);
        return LocalMachineWriteAttempt.Succeeded(id);
    }

    /// <summary>
    /// Everything that already refers to the machine, counted in one place so
    /// <see cref="MachineDeletionPolicy"/> can say what is in the way. These are the four tables that
    /// hold <c>Machines.Id</c>; the provider's own stored data names the provider's machine number
    /// and is not a reference to this row (AGENTS.md § Database and migrations), which is why a
    /// machine the provider names is refused outright rather than counted.
    /// </summary>
    private async Task<LocalMachineDeletionReferences> CountReferencesAsync(
        long id, CancellationToken cancellationToken) => new(
            MachineStockCounts: await _db.MachineStockCounts
                .CountAsync(count => count.MachineId == id, cancellationToken),
            StockMovements: await _db.StockAdjustments
                .CountAsync(adjustment => adjustment.MachineId == id, cancellationToken),
            PickListRefills: await _db.PickListRefills
                .CountAsync(refill => refill.MachineId == id, cancellationToken),
            OperatingExpenses: await _db.OperatingExpenses
                .CountAsync(expense => expense.MachineId == id, cancellationToken));

    /// <summary>
    /// Whether SQLite refused the statement because one of the restricted machine foreign keys
    /// pointed at the row - the database's own backstop under the count above.
    /// </summary>
    private static bool IsForeignKeyViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(ForeignKeyViolationMessage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The chosen machines with the four reference counts beside them, in one statement: the counts
    /// are correlated subqueries rather than a read per machine, so a list of machines stays one
    /// round trip. The caller filters and orders <paramref name="machines"/> on the entity, because
    /// neither translates once the projection has happened. Tenant scoping is the central query
    /// filter, here as everywhere else in this adapter.
    /// </summary>
    private IQueryable<MachineRow> Rows(IQueryable<LocalMachine> machines) =>
        machines
            .Select(machine => new MachineRow(
                machine.Id,
                machine.Name,
                machine.IsActive,
                machine.ExternalMachineId != null,
                _db.MachineStockCounts.Count(count => count.MachineId == machine.Id),
                _db.StockAdjustments.Count(adjustment => adjustment.MachineId == machine.Id),
                _db.PickListRefills.Count(refill => refill.MachineId == machine.Id),
                _db.OperatingExpenses.Count(expense => expense.MachineId == machine.Id)));

    /// <summary>
    /// Turns the rows into records with their deletion eligibility decided by the one rule
    /// <see cref="DeleteAsync"/> applies (issue #896). The business's sales provider is read once,
    /// and only when a machine on the page carries a provider id - it is needed solely to name the
    /// provider in that machine's refusal.
    /// </summary>
    private async Task<IReadOnlyList<LocalMachineRecord>> WithDeletionEligibilityAsync(
        List<MachineRow> rows, CancellationToken cancellationToken)
    {
        var provider = rows.Exists(row => row.IsProviderNamed)
            ? await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken)
            : null;

        return rows
            .Select(row => new LocalMachineRecord(row.Id, row.Name, row.IsActive, row.IsProviderNamed)
            {
                DeletionEligibility = LocalMachineDeletionEligibility.Decide(
                    row.IsProviderNamed, provider, row.References),
            })
            .ToList();
    }

    /// <summary>One machine as the reads project it, with what refers to it counted alongside.</summary>
    private sealed record MachineRow(
        long Id,
        string Name,
        bool IsActive,
        bool IsProviderNamed,
        int MachineStockCounts,
        int StockMovements,
        int PickListRefills,
        int OperatingExpenses)
    {
        public LocalMachineDeletionReferences References =>
            new(MachineStockCounts, StockMovements, PickListRefills, OperatingExpenses);
    }
}
