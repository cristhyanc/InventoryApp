using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.DTOs;

public record SiteCommissionAgreementDto(long SiteId, DateTime EffectiveFrom, DateTime? EffectiveTo, decimal CommissionRate,
    CommissionFrequency Frequency, CommissionBasis Basis, int? PaymentDueDaysAfterPeriodEnd);
public record CommissionPaymentDto(DateTime PaymentDate, decimal Amount, string? Notes);
public record SiteCommissionMachineDto(long MachineId, string MachineName, int TransactionCount, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionDue, bool IsComplete = true, bool HasConfigurationGap = false, bool HasOverlap = false);
public record SiteCommissionProductDto(string ProductName, int TotalVends, decimal TotalSales);
public record SiteCommissionRowDto(long SiteId, string SiteName, DateTime PeriodStart, DateTime PeriodEnd, CommissionFrequency Frequency,
    CommissionBasis Basis, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionRate,
    decimal CommissionDue, decimal Paid, decimal Outstanding, DateTime? DueDate, string Status, IReadOnlyList<SiteCommissionMachineDto> Machines,
    IReadOnlyList<SiteCommissionProductDto> Products,
    IReadOnlyList<Inventory.Domain.FinancialConfiguration.CommissionPayment> Payments,
    string? DataQuality = null)
{
    public bool HasConfigurationGap { get; init; }
    public bool HasOverlap { get; init; }
    public bool UsesMultipleRates { get; init; }
}
public record SiteCommissionReportDto(DateTime From, DateTime To, IReadOnlyList<SiteCommissionRowDto> Rows)
{
    /// <summary>
    /// Whether this is an answer about commission at all (issue #731). <c>false</c> only when the
    /// provider is not usable, where the empty row list means "nothing could be calculated" and must
    /// never be read as "no site is owed anything"; the request still succeeds with <c>200</c>,
    /// because an unconnected provider is a state of this business and not a failure of the call.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// The stable reason code for an unavailable report - <c>NayaxNotConnected</c> or
    /// <c>PermissionNotGranted</c>, the same two codes the reorder and dashboard surfaces publish -
    /// and null for an available one. A code a client branches on, never a sentence to display.
    /// </summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The one sentence that explains an unavailable report; null for an available one.</summary>
    public string? UnavailableDetail { get; init; }
}
