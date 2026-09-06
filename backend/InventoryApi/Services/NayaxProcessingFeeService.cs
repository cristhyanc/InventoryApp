using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class NayaxProcessingFeeService : INayaxProcessingFeeService
{
    private readonly AppDbContext _db;

    public NayaxProcessingFeeService(AppDbContext db) => _db = db;

    public async Task<NayaxProcessingFeeResult> GetProcessingFeesAsync(
        DateTime fromDate, DateTime toDate, long? machineId = null, CancellationToken cancellationToken = default)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to < from) (from, to) = (to, from);

        var reimbursements = await _db.ImportedReimbursements.AsNoTracking()
            .Where(x => x.ReimbursementStartDate.HasValue && x.ReimbursementEndDate.HasValue &&
                x.ReimbursementStartDate.Value.Date <= to && x.ReimbursementEndDate.Value.Date >= from)
            .Include(x => x.Fees)
            .Include(x => x.Devices)
            .ToListAsync(cancellationToken);

        var actualByDay = new Dictionary<DateTime, decimal>();
        var actualGstByDay = new Dictionary<DateTime, decimal>();
        foreach (var reimbursement in reimbursements)
        {
            var start = reimbursement.ReimbursementStartDate!.Value.Date;
            var end = reimbursement.ReimbursementEndDate!.Value.Date;
            var reimbursementActualExGst = machineId.HasValue
                ? reimbursement.Devices
                    .Where(d => long.TryParse(d.MachineNumber, out var parsed) && parsed == machineId.Value && d.ProcessingFee.HasValue)
                    .Sum(d => d.ProcessingFee!.Value)
                : reimbursement.Fees
                    .Where(f => !f.IsPreviousPeriod && IsProcessingFee(f))
                    .Sum(FeeExGst);
            var reimbursementActualGst = machineId.HasValue
                ? ReportingCalculations.GstFromExcluding(reimbursementActualExGst)
                : reimbursement.Fees.Where(f => !f.IsPreviousPeriod && IsProcessingFee(f)).Sum(FeeGst);

            var hasAuthoritativeFee = machineId.HasValue
                ? reimbursement.Devices.Any(d => long.TryParse(d.MachineNumber, out var parsed) && parsed == machineId.Value && d.ProcessingFee.HasValue)
                : reimbursement.Fees.Any(f => !f.IsPreviousPeriod && IsProcessingFee(f));
            if (!hasAuthoritativeFee)
                continue;

            var days = (end - start).Days + 1;
            var dailyAmount = reimbursementActualExGst / days;
            var dailyGst = reimbursementActualGst / days;
            for (var day = start; day <= end; day = day.AddDays(1))
                if (day >= from && day <= to)
                {
                    actualByDay[day] = actualByDay.GetValueOrDefault(day) + dailyAmount;
                    actualGstByDay[day] = actualGstByDay.GetValueOrDefault(day) + dailyGst;
                }
        }

        var eligibleSales = await _db.NayaxSales.AsNoTracking()
            .Where(s => s.MachineAuthorizationTime >= from && s.MachineAuthorizationTime < to.AddDays(1) &&
                (!machineId.HasValue || s.MachineID == machineId.Value) && s.TransactionStatusId == 12)
            .Select(s => new { s.MachineAuthorizationTime, s.PaymentMethod })
            .ToListAsync(cancellationToken);
        var rates = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .OrderBy(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken);

        var estimatedCardTransactions = 0;
        var estimatedExGst = 0m;
        DateTime? estimatedFrom = null;
        foreach (var sale in eligibleSales)
        {
            var day = sale.MachineAuthorizationTime.Date;
            if (actualByDay.ContainsKey(day) || PaymentMethodClassifier.Classify(sale.PaymentMethod) != NayaxPaymentType.Card)
                continue;

            var rate = rates.LastOrDefault(r => r.EffectiveFrom.Date <= day)?.FeeExGst ?? 0m;
            estimatedExGst += rate;
            estimatedCardTransactions++;
            estimatedFrom = estimatedFrom is null || day < estimatedFrom ? day : estimatedFrom;
        }

        var actualExGst = actualByDay.Values.Sum();
        var actualGst = actualGstByDay.Values.Sum();
        var estimatedGst = ReportingCalculations.GstFromExcluding(estimatedExGst);
        return new NayaxProcessingFeeResult(
            actualExGst, actualGst, actualExGst + actualGst,
            estimatedExGst, estimatedGst, estimatedExGst + estimatedGst,
            estimatedCardTransactions, actualByDay.Count == 0 ? null : actualByDay.Keys.Max(), estimatedFrom);
    }

    private static bool IsProcessingFee(ImportedFee fee) =>
        (fee.FeesTypeId ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase) ||
        (fee.FeeTypeDescription ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase);

    private static decimal FeeExGst(ImportedFee fee)
    {
        if (fee.TotalSum.HasValue) return fee.TotalSum.Value;
        if (!fee.TotalSumWithVat.HasValue) return 0m;
        var gst = fee.VatPercentage.HasValue
            ? fee.TotalSumWithVat.Value * fee.VatPercentage.Value / (100m + fee.VatPercentage.Value)
            : ReportingCalculations.GstFromInclusive(fee.TotalSumWithVat.Value);
        return fee.TotalSumWithVat.Value - gst;
    }

    private static decimal FeeGst(ImportedFee fee)
    {
        if (fee.TotalSumWithVat.HasValue && fee.TotalSum.HasValue)
            return fee.TotalSumWithVat.Value - fee.TotalSum.Value;
        if (fee.TotalSumWithVat.HasValue && fee.VatPercentage.HasValue)
            return fee.TotalSumWithVat.Value * fee.VatPercentage.Value / (100m + fee.VatPercentage.Value);
        return 0m;
    }
}
