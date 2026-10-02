using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record SiteCommissionSaleFact(
    long MachineId,
    DateTime MachineAuthorizationTime,
    decimal SettlementValue,
    string? PaymentMethod,
    string? ProductName);

public interface ISiteCommissionStore
{
    Task<IReadOnlyList<SiteCommissionSaleFact>> GetCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds,
        DateTime from,
        DateTime endExclusive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionAgreement>> GetAgreementsAsync(
        long? siteId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionPayment>> GetPaymentsAsync(
        long? siteId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken);

    Task<bool> HasOverlappingAgreementAsync(
        long siteId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        CancellationToken cancellationToken);

    Task<CommissionAgreement> AddAgreementAsync(
        CommissionAgreement agreement,
        CancellationToken cancellationToken);

    Task<CommissionPayment> AddPaymentAsync(
        CommissionPayment payment,
        CancellationToken cancellationToken);
}
