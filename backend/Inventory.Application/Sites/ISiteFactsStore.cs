namespace Inventory.Application.Sites;

/// <summary>Whether a catalogue product is active, for the low/empty stock alert rule.</summary>
public sealed record SiteProductActivityFact(long ProductId, bool IsActive);

/// <summary>A completed sale already filtered to a machine within the requested lookback window.</summary>
public sealed record SiteCompletedSaleFact(long MachineId, decimal SettlementValue, DateTime MachineAuthorizationTime);

/// <summary>
/// A site's resolved card-sale commission facts as of a date: whether the site's commission
/// configuration is unavailable (an ambiguous/overlapping agreement), and, when it is not, the
/// commission amount for each of the given candidate retail prices, resolved with the one
/// authoritative Domain <c>SiteCommissionCalculator</c> formula. The amounts are meaningless (and omitted)
/// when <see cref="ConfigurationUnavailable"/> is true.
/// </summary>
public sealed record SiteCardCommissionResolution(
    bool ConfigurationUnavailable,
    IReadOnlyDictionary<decimal, decimal> CommissionAmountByRetailPrice);

/// <summary>
/// Narrow facts port for the site dashboard, owned by the Application layer. Its temporary EF Core
/// implementation applies the Domain-owned commission and effective-configuration rules
/// (see <c>docs/architecture.md</c>).
/// </summary>
public interface ISiteFactsStore
{
    Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The completed sales of <paramref name="machineIds"/> authorized at or after
    /// <paramref name="fromUtc"/>.
    ///
    /// <para>The bound is <b>inclusive</b> (issue #1012), because its caller derives it from the
    /// first instant of the earliest period it publishes a figure for: a sale authorized at exactly
    /// a period's start belongs to that period, which is the comparison the figures themselves make,
    /// so an exclusive bound would make that period's own first instant unreachable.</para>
    /// </summary>
    Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime fromUtc, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, Inventory.Domain.Sites.SiteProductCostBasis>> GetProductCostBasisAsync(
        CancellationToken cancellationToken);

    Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken);

    Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken);
}
