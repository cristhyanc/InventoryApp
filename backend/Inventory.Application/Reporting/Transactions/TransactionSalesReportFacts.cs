using Inventory.Domain.Reporting.Transactions;

namespace Inventory.Application.Reporting.Transactions;

/// <summary>
/// Raw, per-transaction facts needed to build the transaction sales report for a resolved date
/// range and optional machine filter. Contains no financial formulas or product-matching logic:
/// those live in <c>Inventory.Domain.Reporting.Transactions</c>/<c>ProductMatching</c> and in this
/// feature's use case. <see cref="Transactions"/> is an asynchronous stream, not a materialised list:
/// the provider consumes its underlying query one row at a time instead of completing it with
/// <c>ToListAsync</c>, and the use case enumerates it exactly once.
/// </summary>
public sealed record TransactionSalesReportFacts(
    IAsyncEnumerable<TransactionSalesReportFactsRow> Transactions,
    IReadOnlyList<TransactionSalesCatalogueEntry> ProductCatalogue,
    IReadOnlyList<EffectiveFeeRate> FeeRates,
    IReadOnlyList<EffectiveCommissionAgreement> CommissionAgreements,
    // True when the live Nayax machine directory could not be retrieved, so SiteId/SiteName below
    // are unavailable for every row rather than genuinely unmapped.
    bool SiteMappingUnavailable);

/// <summary>One raw transaction, before product matching and Domain fee/commission/profit derivation.</summary>
public sealed record TransactionSalesReportFactsRow(
    long TransactionId,
    DateTime TransactionDate,
    long MachineId,
    // Raw persisted machine name; null when the imported sale never carried one. The use case
    // applies the "Machine {id}" display fallback, so search matching (against the raw value) and
    // display/sort (against the fallback-applied value) stay distinguishable.
    string? MachineName,
    long? SiteId,
    string? SiteName,
    long? NayaxProductId,
    string? RawProductName,
    TransactionPaymentType PaymentType,
    string? RawPaymentMethod,
    decimal Sale,
    decimal? NayaxProductCostPrice,
    decimal? UnitCostAtSale,
    decimal? CostOfGoodsSold,
    string CostingStatus,
    string CostSource,
    bool HasPersistedCost,
    TransactionSaleStatus Status,
    int? TransactionStatusId,
    string TransactionStatusDescription);

/// <summary>A catalogue product candidate available for matching, with its display name.</summary>
public sealed record TransactionSalesCatalogueEntry(long Id, string Name);
