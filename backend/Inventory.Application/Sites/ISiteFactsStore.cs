namespace Inventory.Application.Sites;

/// <summary>Whether a catalogue product is active, for the low/empty stock alert rule.</summary>
public sealed record SiteProductActivityFact(long ProductId, bool IsActive);

/// <summary>A completed sale already filtered to a machine within the requested lookback window.</summary>
public sealed record SiteCompletedSaleFact(long MachineId, decimal SettlementValue, DateTime MachineAuthorizationTime);

/// <summary>
/// A site's resolved card-sale commission facts as of a date: whether the site's commission
/// configuration is unavailable (an ambiguous/overlapping agreement), and, when it is not, the
/// commission amount for each of the given candidate retail prices, resolved with the one
/// authoritative <c>SiteCommissionCalculator</c> formula. The amounts are meaningless (and omitted)
/// when <see cref="ConfigurationUnavailable"/> is true.
/// </summary>
public sealed record SiteCardCommissionResolution(
    bool ConfigurationUnavailable,
    IReadOnlyDictionary<decimal, decimal> CommissionAmountByRetailPrice);

/// <summary>
/// Narrow persistence/business-service port for the site dashboard, owned by the Application layer.
/// Its temporary EF Core implementation composes the still-legacy <c>SiteCommissionCalculator</c>/
/// <c>EffectiveFinancialConfiguration</c> (see <c>docs/architecture.md</c>).
/// </summary>
public interface ISiteFactsStore
{
    Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, Inventory.Domain.Sites.SiteProductCostBasis>> GetProductCostBasisAsync(
        CancellationToken cancellationToken);

    Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken);

    Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken);
}
