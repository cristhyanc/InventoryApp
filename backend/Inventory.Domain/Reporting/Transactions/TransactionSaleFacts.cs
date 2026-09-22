namespace Inventory.Domain.Reporting.Transactions;

/// <summary>Mirrors <c>InventoryApi.Services.NayaxPaymentType</c> so Domain never references it.</summary>
public enum TransactionPaymentType { Card, Cash, Unknown }

/// <summary>Mirrors <c>InventoryApi.Services.NayaxTransactionStatus</c> so Domain never references it.</summary>
public enum TransactionSaleStatus { Unknown, Completed, Pending, Refunded, CancelledOrDeclined }

/// <summary>Mirrors <c>InventoryApi.Models.CommissionBasis</c> so Domain never references it.</summary>
public enum TransactionCommissionBasis { GrossSales, CardSales, SalesExGst }

/// <summary>An effective-dated Nayax processing fee rate, independent of the persistence entity.</summary>
public readonly record struct EffectiveFeeRate(DateTime EffectiveFrom, decimal FeeExGst);

/// <summary>An effective-dated site commission agreement, independent of the persistence entity.</summary>
public readonly record struct EffectiveCommissionAgreement(
    long SiteId, DateTime EffectiveFrom, DateTime? EffectiveTo, TransactionCommissionBasis Basis, decimal CommissionRate);
