using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Domain.Reporting.Transactions;

public readonly record struct EffectiveCommissionAgreement(
    long SiteId,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    CommissionBasis Basis,
    decimal CommissionRate);
