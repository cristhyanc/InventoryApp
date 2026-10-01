using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record SiteCommissionMachineReportRow(
    long MachineId,
    string MachineName,
    int TransactionCount,
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal EligibleSales,
    decimal CommissionDue,
    bool IsComplete = true,
    bool HasConfigurationGap = false,
    bool HasOverlap = false);

public sealed record SiteCommissionProductReportRow(
    string ProductName,
    int TotalVends,
    decimal TotalSales);

public sealed record SiteCommissionReportRow(
    long SiteId,
    string SiteName,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    CommissionFrequency Frequency,
    CommissionBasis Basis,
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal EligibleSales,
    decimal CommissionRate,
    decimal CommissionDue,
    decimal Paid,
    decimal Outstanding,
    DateTime? DueDate,
    string Status,
    IReadOnlyList<SiteCommissionMachineReportRow> Machines,
    IReadOnlyList<SiteCommissionProductReportRow> Products,
    IReadOnlyList<CommissionPayment> Payments,
    string? DataQuality = null)
{
    public bool HasConfigurationGap { get; init; }
    public bool HasOverlap { get; init; }
    public bool UsesMultipleRates { get; init; }
}

public sealed record SiteCommissionReport(
    DateTime From,
    DateTime To,
    IReadOnlyList<SiteCommissionReportRow> Rows);
