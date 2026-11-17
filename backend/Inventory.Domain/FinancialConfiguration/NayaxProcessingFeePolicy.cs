using Inventory.Domain.Reporting;

namespace Inventory.Domain.FinancialConfiguration;

public sealed record ImportedProcessingFee(
    string? FeesTypeId,
    string? FeeTypeDescription,
    bool IsPreviousPeriod,
    decimal? TotalSum,
    decimal? TotalSumWithVat,
    decimal? VatPercentage);

public sealed record ImportedProcessingFeeDevice(string? MachineNumber, decimal? ProcessingFee);

public sealed record ProcessingFeeReimbursement(
    DateTime StartDate,
    DateTime EndDate,
    IReadOnlyList<ImportedProcessingFee> Fees,
    IReadOnlyList<ImportedProcessingFeeDevice> Devices);

public sealed record CompletedCardTransaction(DateTime MachineAuthorizationTime, string? PaymentMethod)
{
    /// <summary>
    /// The calendar day this transaction's processing fee belongs to: the day whose authoritative
    /// imported fee covers it, and otherwise the day whose effective rate estimates it. Null means the
    /// date part of <see cref="MachineAuthorizationTime"/>, which is what a caller asking for a range
    /// of calendar dates means. A caller whose period is bounded by business
    /// days supplies the sale's business date instead, so a sale is charged a fee on the same day its
    /// revenue was counted even when the two dates differ (issue #310).
    /// </summary>
    public DateTime? FeeDate { get; init; }
}

public sealed record NayaxProcessingFeeTotals(
    decimal ActualFeeExGst,
    decimal ActualFeeGst,
    decimal ActualFeeIncGst,
    decimal EstimatedFeeExGst,
    decimal EstimatedFeeGst,
    decimal EstimatedFeeIncGst,
    int EstimatedCardTransactionCount,
    DateTime? ActualFeeCoverageEndDate,
    DateTime? EstimatedFeeFromDate,
    int MissingRateTransactionCount);

public static class NayaxProcessingFeePolicy
{
    public static NayaxProcessingFeeTotals Calculate(
        DateTime fromDate,
        DateTime toDate,
        long? machineId,
        IReadOnlyList<ProcessingFeeReimbursement> reimbursements,
        IReadOnlyList<CompletedCardTransaction> eligibleSales,
        IReadOnlyList<EffectiveNayaxFeeRate> rates)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to < from) (from, to) = (to, from);

        var actualByDay = new Dictionary<DateTime, decimal>();
        var actualGstByDay = new Dictionary<DateTime, decimal>();
        foreach (var reimbursement in reimbursements)
        {
            var start = reimbursement.StartDate.Date;
            var end = reimbursement.EndDate.Date;
            var matchingFees = reimbursement.Fees.Where(fee => !fee.IsPreviousPeriod && IsProcessingFee(fee)).ToList();
            var matchingDevices = reimbursement.Devices.Where(device =>
                long.TryParse(device.MachineNumber, out var parsed) && parsed == machineId && device.ProcessingFee.HasValue).ToList();
            var reimbursementActualExGst = machineId.HasValue
                ? matchingDevices.Sum(device => device.ProcessingFee!.Value)
                : matchingFees.Sum(FeeExGst);
            var reimbursementActualGst = machineId.HasValue
                ? ReportingCalculations.GstFromExcluding(reimbursementActualExGst)
                : matchingFees.Sum(FeeGst);

            var hasAuthoritativeFee = machineId.HasValue ? matchingDevices.Count != 0 : matchingFees.Count != 0;
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

        var estimatedCardTransactions = 0;
        var missingRateTransactions = 0;
        var estimatedExGst = 0m;
        DateTime? estimatedFrom = null;
        foreach (var sale in eligibleSales)
        {
            var day = DateTime.SpecifyKind((sale.FeeDate ?? sale.MachineAuthorizationTime).Date, DateTimeKind.Unspecified);
            if (actualByDay.ContainsKey(day) || PaymentMethodClassifier.Classify(sale.PaymentMethod) != NayaxPaymentType.Card)
                continue;

            var rate = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, day);
            if (rate is null)
            {
                missingRateTransactions++;
                continue;
            }

            estimatedExGst += rate.Value.FeeExGst;
            estimatedCardTransactions++;
            estimatedFrom = estimatedFrom is null || day < estimatedFrom ? day : estimatedFrom;
        }

        var actualExGst = actualByDay.Values.Sum();
        var actualGst = actualGstByDay.Values.Sum();
        var estimatedGst = ReportingCalculations.GstFromExcluding(estimatedExGst);
        return new NayaxProcessingFeeTotals(
            actualExGst, actualGst, actualExGst + actualGst,
            estimatedExGst, estimatedGst, estimatedExGst + estimatedGst,
            estimatedCardTransactions, actualByDay.Count == 0 ? null : actualByDay.Keys.Max(), estimatedFrom,
            missingRateTransactions);
    }

    private static bool IsProcessingFee(ImportedProcessingFee fee) =>
        (fee.FeesTypeId ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase) ||
        (fee.FeeTypeDescription ?? string.Empty).Contains("processing", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One imported fee row's amount excluding GST. An ex-GST amount the import recorded is
    /// authoritative; otherwise it is the inclusive amount less <see cref="FeeGst"/>, so the two
    /// halves always add back up to the total the import recorded (issue #1257).
    /// </summary>
    private static decimal FeeExGst(ImportedProcessingFee fee)
    {
        if (fee.TotalSum.HasValue) return fee.TotalSum.Value;
        if (!fee.TotalSumWithVat.HasValue) return 0m;
        return fee.TotalSumWithVat.Value - FeeGst(fee);
    }

    /// <summary>
    /// One imported fee row's GST, from the strongest evidence the row carries: both amounts, then
    /// its own VAT percentage, and otherwise the inclusive amount under the current Australian GST
    /// assumption (AGENTS.md § Nayax processing fees and GST).
    ///
    /// <para><strong>Issue #1257 fixed the last of those.</strong> A row with only an inclusive
    /// amount used to report GST <c>0</c> while <see cref="FeeExGst"/> had already removed one
    /// eleventh for it, so an 11.00 fee was reported as 10.00 ex GST, 0.00 GST and 10.00 including
    /// GST - a dollar of the business's own fee GST credit lost, and a fee total that was not the
    /// one imported. A row that records neither amount is still nothing, not a zero-GST fee.</para>
    /// </summary>
    private static decimal FeeGst(ImportedProcessingFee fee)
    {
        if (fee.TotalSumWithVat.HasValue && fee.TotalSum.HasValue)
            return fee.TotalSumWithVat.Value - fee.TotalSum.Value;
        if (!fee.TotalSumWithVat.HasValue) return 0m;
        return fee.VatPercentage.HasValue
            ? fee.TotalSumWithVat.Value * fee.VatPercentage.Value / (100m + fee.VatPercentage.Value)
            : ReportingCalculations.GstFromInclusive(fee.TotalSumWithVat.Value);
    }
}
