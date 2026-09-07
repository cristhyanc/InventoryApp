using InventoryApi.Models;

namespace InventoryApi.DTOs;

public record SiteCommissionAgreementDto(long SiteId, DateTime EffectiveFrom, DateTime? EffectiveTo, decimal CommissionRate,
    CommissionFrequency Frequency, CommissionBasis Basis, int? PaymentDueDaysAfterPeriodEnd);
public record CommissionPaymentDto(DateTime PaymentDate, decimal Amount, string? Notes);
public record SiteCommissionMachineDto(long MachineId, string MachineName, int TransactionCount, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionDue);
public record SiteCommissionRowDto(long SiteId, string SiteName, DateTime PeriodStart, DateTime PeriodEnd, CommissionFrequency Frequency,
    CommissionBasis Basis, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionRate,
    decimal CommissionDue, decimal Paid, decimal Outstanding, DateTime? DueDate, string Status, IReadOnlyList<SiteCommissionMachineDto> Machines,
    IReadOnlyList<CommissionPayment> Payments, string? DataQuality = null);
public record SiteCommissionReportDto(DateTime From, DateTime To, IReadOnlyList<SiteCommissionRowDto> Rows);
