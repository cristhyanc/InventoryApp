using Inventory.Application.NayaxProcessingFees;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

public sealed class EfNayaxProcessingFeeFactsProvider : INayaxProcessingFeeFactsProvider
{
    private readonly AppDbContext _db;

    public EfNayaxProcessingFeeFactsProvider(AppDbContext db)
    {
        _db = db;
    }

    public async Task<NayaxProcessingFeeFacts> GetFactsAsync(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        CancellationToken cancellationToken)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to < from) (from, to) = (to, from);

        var reimbursements = await ReimbursementFactsAsync(from, to, cancellationToken);

        var completedSales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineAuthorizationTime >= from &&
                sale.MachineAuthorizationTime < to.AddDays(1) &&
                (!machineId.HasValue || sale.MachineID == machineId.Value))
            .Where(EfNayaxSalesQueries.CompletedSalePredicate)
            .Select(sale => new CompletedCardTransaction(sale.MachineAuthorizationTime, sale.PaymentMethod))
            .ToListAsync(cancellationToken);

        return new NayaxProcessingFeeFacts(reimbursements, completedSales);
    }

    public async Task<NayaxProcessingFeeFacts> GetBusinessPeriodFactsAsync(
        NayaxProcessingFeeBusinessPeriod period,
        long? machineId,
        CancellationToken cancellationToken)
    {
        var reimbursements = await ReimbursementFactsAsync(
            period.FirstBusinessDate.Date, period.LastBusinessDate.Date, cancellationToken);

        // Selected between the period's own UTC instants, inclusive at both ends, exactly as the
        // dashboard selects the sales whose revenue it reports - not by the whole UTC dates those
        // instants fall on, which for a Sydney business day spans two of them (issue #310).
        var completedSales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineAuthorizationTime >= period.StartUtc &&
                sale.MachineAuthorizationTime <= period.EndUtc &&
                (!machineId.HasValue || sale.MachineID == machineId.Value))
            .Where(EfNayaxSalesQueries.CompletedSalePredicate)
            .Select(sale => new CompletedCardTransaction(sale.MachineAuthorizationTime, sale.PaymentMethod))
            .ToListAsync(cancellationToken);

        return new NayaxProcessingFeeFacts(reimbursements, completedSales);
    }

    private async Task<List<ProcessingFeeReimbursement>> ReimbursementFactsAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(reimbursement =>
                reimbursement.ReimbursementStartDate.HasValue &&
                reimbursement.ReimbursementEndDate.HasValue &&
                reimbursement.ReimbursementStartDate.Value.Date <= to &&
                reimbursement.ReimbursementEndDate.Value.Date >= from)
            .Select(reimbursement => new
            {
                reimbursement.Id,
                StartDate = reimbursement.ReimbursementStartDate!.Value,
                EndDate = reimbursement.ReimbursementEndDate!.Value
            })
            .ToListAsync(cancellationToken);

        var reimbursementIds = reimbursements.Select(reimbursement => reimbursement.Id).ToList();
        var fees = reimbursementIds.Count == 0
            ? []
            : await _db.ImportedFees.AsNoTracking()
                .Where(fee => reimbursementIds.Contains(fee.ImportedReimbursementId))
                .Select(fee => new
                {
                    fee.ImportedReimbursementId,
                    fee.FeesTypeId,
                    fee.FeeTypeDescription,
                    fee.IsPreviousPeriod,
                    fee.TotalSum,
                    fee.TotalSumWithVat,
                    fee.VatPercentage
                })
                .ToListAsync(cancellationToken);
        var devices = reimbursementIds.Count == 0
            ? []
            : await _db.ImportedReimbursementDevices.AsNoTracking()
                .Where(device => reimbursementIds.Contains(device.ImportedReimbursementId))
                .Select(device => new
                {
                    device.ImportedReimbursementId,
                    device.MachineNumber,
                    device.ProcessingFee
                })
                .ToListAsync(cancellationToken);

        return reimbursements.Select(reimbursement =>
            new ProcessingFeeReimbursement(
                reimbursement.StartDate,
                reimbursement.EndDate,
                fees.Where(fee => fee.ImportedReimbursementId == reimbursement.Id)
                    .Select(fee => new ImportedProcessingFee(
                        fee.FeesTypeId,
                        fee.FeeTypeDescription,
                        fee.IsPreviousPeriod,
                        fee.TotalSum,
                        fee.TotalSumWithVat,
                        fee.VatPercentage))
                    .ToList(),
                devices.Where(device => device.ImportedReimbursementId == reimbursement.Id)
                    .Select(device => new ImportedProcessingFeeDevice(device.MachineNumber, device.ProcessingFee))
                    .ToList()))
            .ToList();
    }
}
