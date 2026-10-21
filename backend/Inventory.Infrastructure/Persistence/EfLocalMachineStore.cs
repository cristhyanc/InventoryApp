using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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
/// <para><strong>Nothing here reads or writes <see cref="LocalMachine.ExternalMachineId"/>.</strong>
/// A created machine has none, a rename leaves it alone, and the records this adapter returns carry
/// only whether one is present. The column is written by the <c>AddMachines</c> backfill and, later,
/// by provider synchronization and the provider machine review (issue #742).</para>
/// </summary>
public sealed class EfLocalMachineStore : ILocalMachineStore
{
    private readonly AppDbContext _db;

    public EfLocalMachineStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LocalMachineRecord>> ListOrderedByNameAsync(
        CancellationToken cancellationToken) =>
        await _db.Machines
            .AsNoTracking()
            .OrderBy(machine => machine.Name)
            .ThenBy(machine => machine.Id)
            .Select(machine => new LocalMachineRecord(
                machine.Id, machine.Name, machine.IsActive, machine.ExternalMachineId != null))
            .ToListAsync(cancellationToken);

    public async Task<LocalMachineRecord?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        await _db.Machines
            .AsNoTracking()
            .Where(machine => machine.Id == id)
            .Select(machine => new LocalMachineRecord(
                machine.Id, machine.Name, machine.IsActive, machine.ExternalMachineId != null))
            .FirstOrDefaultAsync(cancellationToken);

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
            cancellationToken);
    }

    public Task<LocalMachineWriteAttempt> SetActiveAsync(
        long id, bool isActive, CancellationToken cancellationToken) =>
        WriteAsync(id, machine => machine.IsActive = isActive, cancellationToken);

    /// <summary>
    /// Reads the machine and applies <paramref name="change"/> to it in one transaction, so the
    /// existence check and the write it depends on cannot be separated by another request deleting
    /// or replacing the row.
    /// </summary>
    private async Task<LocalMachineWriteAttempt> WriteAsync(
        long id, Action<LocalMachine> change, CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var machine = await _db.Machines.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (machine is null) return LocalMachineWriteAttempt.NotFound();

        change(machine);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return LocalMachineWriteAttempt.Succeeded(machine.Id);
    }
}
