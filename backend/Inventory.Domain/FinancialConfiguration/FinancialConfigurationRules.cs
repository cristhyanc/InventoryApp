using Inventory.Domain.Reporting;

namespace Inventory.Domain.FinancialConfiguration;

public enum NayaxPaymentType
{
    Card,
    Cash,
    Unknown
}

public enum NayaxTransactionStatus
{
    Unknown,
    Completed,
    Pending,
    Refunded,
    CancelledOrDeclined
}

public enum CommissionFrequency
{
    None,
    Monthly,
    Quarterly
}

public enum CommissionBasis
{
    GrossSales,
    CardSales,
    SalesExGst
}

public sealed record CommissionAgreement(
    int Id,
    long SiteId,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    decimal CommissionRate,
    CommissionFrequency Frequency,
    CommissionBasis Basis,
    int? PaymentDueDaysAfterPeriodEnd,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CommissionPayment(
    int Id,
    long SiteId,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime PaymentDate,
    decimal Amount,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public readonly record struct EffectiveNayaxFeeRate(DateTime EffectiveFrom, decimal FeeExGst);

public static class EffectiveFinancialConfiguration
{
    public static CommissionAgreement? ResolveAgreement(
        IEnumerable<CommissionAgreement> agreements,
        long siteId,
        DateTime effectiveAt)
    {
        var matches = agreements.Where(x =>
            x.SiteId == siteId &&
            x.EffectiveFrom.Date <= effectiveAt.Date &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= effectiveAt.Date)).ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple site commission agreements cover site {siteId} on {effectiveAt:yyyy-MM-dd}.")
        };
    }

    public static EffectiveNayaxFeeRate? ResolveNayaxFeeRate(
        IEnumerable<EffectiveNayaxFeeRate> rates,
        DateTime effectiveAt) =>
        rates
            .Where(x => x.EffectiveFrom.Date <= effectiveAt.Date)
            .OrderByDescending(x => x.EffectiveFrom)
            .Select(x => (EffectiveNayaxFeeRate?)x)
            .FirstOrDefault();
}

public static class SiteCommissionCalculator
{
    public static decimal EligibleSales(CommissionBasis basis, decimal sale, NayaxPaymentType paymentType) =>
        basis switch
        {
            CommissionBasis.CardSales => paymentType == NayaxPaymentType.Card ? sale : 0m,
            CommissionBasis.SalesExGst => sale - ReportingCalculations.GstFromInclusive(sale),
            _ => sale
        };

    public static decimal CommissionAmount(CommissionAgreement agreement, decimal sale, NayaxPaymentType paymentType) =>
        EligibleSales(agreement.Basis, sale, paymentType) * agreement.CommissionRate;
}

public static class PaymentMethodClassifier
{
    public static NayaxPaymentType Classify(string? paymentMethod, string? recognitionDescription = null)
    {
        var result = ClassifySingle(paymentMethod);
        return result == NayaxPaymentType.Unknown ? ClassifySingle(recognitionDescription) : result;
    }

    private static NayaxPaymentType ClassifySingle(string? paymentMethod)
    {
        var value = paymentMethod?.Trim().ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "cash" => NayaxPaymentType.Cash,
            "credit card" or "prepaid credit" => NayaxPaymentType.Card,
            _ when value.Contains("cash") => NayaxPaymentType.Cash,
            _ when value.Contains("credit") || value.Contains("card") => NayaxPaymentType.Card,
            _ => NayaxPaymentType.Unknown
        };
    }
}

public static class NayaxTransactionStatusIds
{
    public const int Completed = 12;
    public const int CancelledOrDeclined26 = 26;
    public const int CashlessCancelledProductNotDispensed = 28;
    public const int CancelledOrDeclined31 = 31;
    public const int PendingSettlementNotFinal = 55;
    public const int Refunded = 62;
    public const int PendingBatch = 80;
    public const int CancelledOrDeclined250 = 250;
}

public static class NayaxTransactionStatusClassifier
{
    public static NayaxTransactionStatus Classify(int? transactionStatusId) =>
        transactionStatusId switch
        {
            NayaxTransactionStatusIds.Completed => NayaxTransactionStatus.Completed,
            NayaxTransactionStatusIds.PendingSettlementNotFinal or NayaxTransactionStatusIds.PendingBatch => NayaxTransactionStatus.Pending,
            NayaxTransactionStatusIds.Refunded => NayaxTransactionStatus.Refunded,
            NayaxTransactionStatusIds.CancelledOrDeclined26 or
            NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed or
            NayaxTransactionStatusIds.CancelledOrDeclined31 or
            NayaxTransactionStatusIds.CancelledOrDeclined250 => NayaxTransactionStatus.CancelledOrDeclined,
            _ => NayaxTransactionStatus.Unknown
        };

    public static bool IsCompletedSale(int? transactionStatusId) =>
        Classify(transactionStatusId) == NayaxTransactionStatus.Completed;

    public static string Describe(int? transactionStatusId) =>
        transactionStatusId switch
        {
            NayaxTransactionStatusIds.Completed => "Approved / Completed",
            NayaxTransactionStatusIds.PendingSettlementNotFinal => "Pending / Settlement not final",
            NayaxTransactionStatusIds.Refunded => "Refunded",
            NayaxTransactionStatusIds.CancelledOrDeclined26 => "Cancelled / declined",
            NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed => "Cashless cancelled - Product could not be dispensed",
            NayaxTransactionStatusIds.CancelledOrDeclined31 => "Cancelled / declined",
            NayaxTransactionStatusIds.PendingBatch => "Pending batch",
            NayaxTransactionStatusIds.CancelledOrDeclined250 => "Cancelled / declined",
            null => "Unknown",
            _ => $"Status {transactionStatusId}"
        };
}
