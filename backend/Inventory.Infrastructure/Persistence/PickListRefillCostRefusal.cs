using Inventory.Application.Costing;
using Inventory.Domain.Machines;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// How both pick-list refill adapters answer a row whose product cannot be costed (issue #1240).
/// It is shared rather than duplicated for the reason <see cref="PickListRefillWriteLock"/> is:
/// <see cref="EfPickListRefillStore"/> and <see cref="EfMultiMachinePickListRefillStore"/> record
/// the same movements for the same contract, and an operator must not be told two different things
/// about the same blocked product depending on which screen they picked from.
///
/// <para><strong>The replay's message names the product; this adds the machine.</strong> A refused
/// pick reaches the operator as one sentence, and in a multi-machine run "which row do I fix?" is a
/// cell rather than a line: the product alone does not identify it. The wording itself is
/// <see cref="PickListRefillPolicy.CostHistoryRefusalMessage"/> in the Domain - this adapter decides
/// no wording of its own - and the machine's name is read here because only the adapter knows it.
/// </para>
///
/// <para><strong>The name is read on the failure path only.</strong> Every allocation's machine is
/// already known by id, and the write lock a refill holds is what every other picker waits on, so
/// resolving names for a pick that is about to succeed would make every pick pay for a message
/// almost none of them need. This read happens inside the refused transaction, which is discarded
/// immediately afterwards.</para>
/// </summary>
internal static class PickListRefillCostRefusal
{
    /// <summary>
    /// The refusal to throw in place of <paramref name="refusal"/>: the same fatal history, stated
    /// with the machine the row was being picked for. <paramref name="machineId"/> is
    /// <see langword="null"/> for a refill that names no machine, which the single-machine contract
    /// allows; the message then names the product alone.
    /// </summary>
    public static async Task<InventoryCostDataQualityException> NamingMachineAsync(
        AppDbContext db,
        long? machineId,
        InventoryCostDataQualityException refusal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        string? machineLabel = null;
        if (machineId is { } id)
        {
            // The tenant query filter scopes this read, so it can only ever name a machine of the
            // business whose pick was refused. A machine row that is somehow missing leaves the
            // label at the bare id rather than failing the refusal.
            var name = await db.Machines
                .AsNoTracking()
                .Where(machine => machine.Id == id)
                .Select(machine => machine.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            machineLabel = MachineLabel.Phrase(id, name);
        }

        return new InventoryCostDataQualityException(
            PickListRefillPolicy.CostHistoryRefusalMessage(refusal.Message, machineLabel));
    }
}
