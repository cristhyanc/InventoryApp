using Inventory.Application.Machines;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
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
/// <para><strong>Nothing here writes <see cref="LocalMachine.ExternalMachineId"/>.</strong>
/// A created machine has none, and the records this adapter returns carry only whether one is
/// present. Whether one is present is also what refuses a rename or a deactivation: a machine the
/// provider names is renamed and retired at the provider, and that refusal is decided against the
/// stored row inside the write's own transaction, so a machine linked between a caller's read and
/// this write is still refused. The column is written by the <c>AddMachines</c> backfill and, since
/// issue #742, by <see cref="EfProviderMachineLinkStore"/> - the provider machine review's own
/// adapter, which is where a link is decided against the stored rows inside its own
/// transaction.</para>
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

    /// <summary>
    /// One statement over two columns of the machines the caller named: no name, no provider link and
    /// nothing correlated from another table, because this is the read a write holds a lock across
    /// (issue #897). Tenant scoping is the central query filter, so a machine of another business is
    /// simply not in the answer.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, LocalMachineActivation>> GetActivationsAsync(
        IReadOnlyCollection<long> machineIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machineIds);

        return await _db.Machines
            .AsNoTracking()
            .Where(machine => machineIds.Contains(machine.Id))
            .Select(machine => new LocalMachineActivation(machine.Id, machine.IsActive))
            .ToDictionaryAsync(machine => machine.MachineId, cancellationToken);
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
}
