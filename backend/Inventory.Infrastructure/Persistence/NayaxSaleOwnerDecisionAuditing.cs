using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The parts of a <see cref="NayaxSaleOwnerDecisionAudit"/> row that an Owner decision
/// (<see cref="EfNayaxSaleReviewStore"/>) and a supersession by later evidence
/// (<see cref="EfNayaxSaleProviderEvidenceStore"/>) record alike (issue #1105): the sale's values
/// before and after, what it contributed to completed sales before and after, and the imported
/// reimbursement period the change touched.
/// </summary>
internal static class NayaxSaleOwnerDecisionAuditing
{
    /// <summary>The values of a sale an audit row records, read before a change.</summary>
    public sealed record SaleSnapshot(
        NayaxSqsVerification State,
        long MachineId,
        decimal SettlementValue,
        DateTime AuthorizationInstantUtc,
        long? NayaxProductId,
        long? ProductId,
        decimal CountedAmount);

    public static SaleSnapshot Snapshot(NayaxSales sale) => new(
        sale.SqsVerification!.Value,
        sale.MachineID,
        sale.SettlementValue,
        sale.MachineAuthorizationTime,
        sale.NayaxProductId,
        sale.ProductId,
        CountedAmount(sale));

    /// <summary>What the sale contributes to completed-sale totals: its amount, or 0 when not counted.</summary>
    public static decimal CountedAmount(NayaxSales sale) =>
        NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification)
            ? sale.SettlementValue
            : 0m;

    /// <summary>
    /// A new audit row for <paramref name="sale"/>, filled from <paramref name="before"/> and the
    /// sale's values now. Its kind, decision, evidence, reason and actor are the caller's to set.
    /// <c>BusinessId</c> is stamped by <c>BusinessOwnershipEnforcer</c>.
    /// </summary>
    public static async Task<NayaxSaleOwnerDecisionAudit> NewRowAsync(
        AppDbContext db,
        NayaxSales sale,
        SaleSnapshot before,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var after = Snapshot(sale);
        var row = new NayaxSaleOwnerDecisionAudit
        {
            NayaxSaleId = sale.Id,
            TransactionId = sale.TransactionID,
            OccurredAtUtc = occurredAtUtc,
            PreviousState = before.State,
            ResultingState = after.State,
            PreviousMachineId = before.MachineId,
            ResultingMachineId = after.MachineId,
            PreviousSettlementValue = before.SettlementValue,
            ResultingSettlementValue = after.SettlementValue,
            PreviousAuthorizationInstantUtc = before.AuthorizationInstantUtc,
            ResultingAuthorizationInstantUtc = after.AuthorizationInstantUtc,
            PreviousNayaxProductId = before.NayaxProductId,
            ResultingNayaxProductId = after.NayaxProductId,
            PreviousProductId = before.ProductId,
            ResultingProductId = after.ProductId,
            PreviousCountedAmount = before.CountedAmount,
            ResultingCountedAmount = after.CountedAmount,
        };

        if (before.CountedAmount != after.CountedAmount
            || before.AuthorizationInstantUtc != after.AuthorizationInstantUtc)
        {
            await FillReconciledPeriodAsync(db, row, before, after, cancellationToken).ConfigureAwait(false);
        }

        return row;
    }

    /// <summary>
    /// Records the imported reimbursement period that covers the sale's old or new date, and how much
    /// the change moved that period's recorded card sales. Reconciliation matches a period by its
    /// coverage dates against the sale's date, exactly as this does, so a period previously shown as
    /// reconciled is named here when the decision changed it.
    /// </summary>
    private static async Task FillReconciledPeriodAsync(
        AppDbContext db,
        NayaxSaleOwnerDecisionAudit row,
        SaleSnapshot before,
        SaleSnapshot after,
        CancellationToken cancellationToken)
    {
        var periods = await db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue)
            .OrderBy(x => x.Id)
            .Select(x => new { From = x.ReimbursementStartDate!.Value, To = x.ReimbursementEndDate!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dates = new[] { before.AuthorizationInstantUtc.Date, after.AuthorizationInstantUtc.Date };
        foreach (var period in periods)
        {
            var from = period.From.Date;
            var to = period.To.Date;
            var beforeIn = dates[0] >= from && dates[0] <= to;
            var afterIn = dates[1] >= from && dates[1] <= to;
            if (!beforeIn && !afterIn)
            {
                continue;
            }

            // What this period's recorded card sales moved by: the amount the sale now contributes
            // inside it, less the amount it contributed inside it before.
            var difference = (afterIn ? after.CountedAmount : 0m) - (beforeIn ? before.CountedAmount : 0m);
            if (difference == 0m)
            {
                continue;
            }

            row.ReconciledPeriodFrom = from;
            row.ReconciledPeriodTo = to;
            row.ReconciledPeriodDifference = difference;
            return;
        }
    }
}
