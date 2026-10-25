using Inventory.Application.Machines;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProviderMachineLinkStore"/> (issue #742, parent #329
/// child G3), beside the <see cref="AppDbContext"/> and the <see cref="LocalMachine"/> entity it
/// writes. It is <see cref="EfCatalogueReviewStore"/> over machines, deliberately the same shape,
/// because the question - "which local row does this provider id mean, and may it?" - is the same
/// question.
///
/// <para><strong>The read-decide-write sequence stays in this adapter, and here that is the point
/// rather than a precedent.</strong> Both of the link refusals are statements about <em>stored</em>
/// rows - this machine already carries a different provider id, and another machine already carries
/// this one - and the whole purpose of the apply is that they are decided in the same transaction as
/// the write they would permit. A use-case read followed by a separate write is a window in which a
/// machine takes the provider id this plan was about to record on a different row, which is exactly
/// the silent identity error the provider-id work exists to end. The rules themselves are not here:
/// <see cref="ProviderMachineLinkPolicy"/> is, and this adapter supplies it the rows it read.</para>
///
/// <para><strong>Identity comes from the external id column, never from a key or a name.</strong> A
/// provider machine resolves to a local machine only through
/// <see cref="LocalMachine.ExternalMachineId"/>; a local key that equals a provider machine number is
/// a coincidence (issue #735) and two machines that share a name are two machines.</para>
///
/// <para><strong>It writes the business's own provider's ids, and refuses to do anything else</strong>
/// (issue #759). Every method asks first whether <c>Business.SalesProvider</c> is Nayax, because the
/// column holds that provider's ids and nothing in it says who issued them.</para>
///
/// <para><strong>Tenant scoping is the central one.</strong> There is no <c>BusinessId</c> predicate
/// anywhere here: a machine of another business is invisible to the read and so unreachable to the
/// write, and a created machine is stamped with the caller's business by
/// <c>BusinessOwnershipEnforcer</c> rather than told which one to belong to.</para>
///
/// <para><strong>It touches nothing but the link.</strong> No sale, stock movement, machine count,
/// expense or cost is read or written, and a machine's name is left exactly as it is unless this
/// adapter created the row itself.</para>
/// </summary>
public sealed class EfProviderMachineLinkStore : IProviderMachineLinkStore
{
    private readonly AppDbContext _db;

    public EfProviderMachineLinkStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>BEGIN IMMEDIATE</c> since issue #896, through the shared
    /// <see cref="SqliteWriteLockTransaction"/>: the machine delete has to be serialised against this
    /// apply, and a deferred transaction takes its write lock only at the first write - after both
    /// operations have read the machine they disagree about. Taking the write lock when the
    /// transaction opens makes the second one wait and then read the first one's committed state, so
    /// the loser is answered with a deliberate refusal rather than a lock error. It changes nothing
    /// about what a link may do.
    /// </remarks>
    public async Task<IProviderMachineLinkTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken) =>
        new ProviderMachineLinkTransaction(
            await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken));

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalMachineLinkRow>> ListLocalMachinesAsync(
        CancellationToken cancellationToken)
    {
        await RequireNayaxSalesProviderAsync(cancellationToken);

        return await _db.Machines
            .AsNoTracking()
            .OrderBy(machine => machine.Name)
            .ThenBy(machine => machine.Id)
            .Select(machine => new LocalMachineLinkRow(
                machine.Id, machine.Name, machine.IsActive, machine.ExternalMachineId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderMachineLinkWritten>> LinkAsync(
        IReadOnlyList<ProviderMachineLinkCommand> commands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        await RequireNayaxSalesProviderAsync(cancellationToken);

        // Re-read inside the caller's transaction, tracked: every link is decided on the machines as
        // they are now, never on the ones a review happened to see. The staged list grows with the
        // machines this operation creates, so two decisions in one apply cannot both claim one
        // provider id.
        var staged = await _db.Machines.ToListAsync(cancellationToken);

        var refusals = new List<string>();
        var results = new List<(LocalMachine Machine, string ExternalMachineId, bool Created)>();

        foreach (var command in commands)
        {
            var carrier = staged.FirstOrDefault(machine => string.Equals(
                machine.ExternalMachineId, command.ExternalMachineId, StringComparison.Ordinal));

            if (command.Action == ProviderMachineLinkAction.CreateNew)
            {
                if (ProviderMachineLinkPolicy.LinkRefusal(
                        command.ExternalMachineId,
                        chosenMachineProviderId: null,
                        anotherMachineCarriesIt: carrier is not null) is { } createRefusal)
                {
                    refusals.Add(createRefusal);
                    continue;
                }

                // The key is the database's own (issue #735's rule, decision A4): nothing assigns Id,
                // and the name is the provider's because this row exists for the provider's machine.
                var created = new LocalMachine
                {
                    Name = command.Name,
                    IsActive = true,
                    ExternalMachineId = command.ExternalMachineId,
                };

                _db.Machines.Add(created);
                staged.Add(created);
                results.Add((created, command.ExternalMachineId, Created: true));
                continue;
            }

            var chosen = staged.FirstOrDefault(machine => machine.Id == command.LocalMachineId);
            if (chosen is null)
            {
                // Invisible to the read through the central tenant query filter, so another
                // business's machine is simply not a machine this caller named.
                refusals.Add(UnknownMachineRefusal);
                continue;
            }

            if (ProviderMachineLinkPolicy.LinkRefusal(
                    command.ExternalMachineId,
                    chosen.ExternalMachineId,
                    anotherMachineCarriesIt: carrier is not null && carrier != chosen) is { } linkRefusal)
            {
                refusals.Add(linkRefusal);
                continue;
            }

            // A machine's name is the operator's own label and nothing synchronises it (issue #740),
            // so linking records the provider's id and leaves the name alone.
            chosen.ExternalMachineId = command.ExternalMachineId;
            results.Add((chosen, command.ExternalMachineId, Created: false));
        }

        if (refusals.Count > 0)
            throw new DomainValidationException("Nothing was applied. " + string.Join(" ", refusals));

        // Saved once, so every machine this operation created has the key the database assigned
        // before the result names it. Nothing is visible outside the caller's transaction until it
        // commits.
        await _db.SaveChangesAsync(cancellationToken);

        return results
            .Select(result => new ProviderMachineLinkWritten(
                result.ExternalMachineId, result.Machine.Id, result.Machine.Name, result.Created))
            .ToList();
    }

    private const string UnknownMachineRefusal =
        "The machine named is not a machine of this business, so nothing was changed.";

    /// <summary>
    /// Refuses to run at all unless the current business's sales provider is Nayax (issue #759), for
    /// the same reason the catalogue review does: <see cref="LocalMachine.ExternalMachineId"/> holds
    /// the id issued by <c>Business.SalesProvider</c>, and nothing in it says which system that was.
    /// Storing the Nayax credentials the fleet read authenticates with is what records that provider,
    /// so in every reachable state the answer is already Nayax; a business where it is not is a
    /// broken invariant rather than a user error.
    /// </summary>
    private async Task RequireNayaxSalesProviderAsync(CancellationToken cancellationToken)
    {
        if (await EfSalesProviderQueries.CurrentBusinessSellsThroughAsync(
                _db, ExternalProvider.Nayax, cancellationToken))
            return;

        throw new InvalidOperationException(
            "Nayax machines cannot be linked for a business whose sales provider is not Nayax. Store "
                + "the business's Nayax credentials first, which is what records its sales provider. "
                + "Nothing was changed.");
    }

    /// <summary>
    /// The caller's transaction, over the shared write lock. It is a no-op when the context is not
    /// relational or already inside somebody else's transaction, in which case commit and rollback
    /// belong to whoever opened that one - the same shape <see cref="EfCatalogueReviewStore"/> uses.
    /// </summary>
    private sealed class ProviderMachineLinkTransaction : IProviderMachineLinkTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;

        public ProviderMachineLinkTransaction(SqliteWriteLockTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
