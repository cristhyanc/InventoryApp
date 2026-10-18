using Inventory.Application.Costing;
using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILatestNayaxSalesStore"/> (issue #187). It lives in
/// Inventory.Infrastructure, following the same pattern as
/// <see cref="EfMachineStockEventStore"/>: <see cref="AppDbContext"/> and the
/// <see cref="NayaxSales"/> persistence model it depends on moved there in issue #307, and this
/// adapter family followed them in issue #309 (Persistence 8/8 of #153).
///
/// The import rules themselves are unchanged from the former private
/// <c>MachineService.SaveMachinesLastSalesAsync</c>: deduplication by the remote
/// <c>TransactionID</c>, product matching through the Domain <see cref="ProductMatcher"/> (issue
/// #301 removed the <c>NayaxProductMatcher</c> wrapper these calls went through; the catalogue is
/// projected onto candidates once per batch and the ID/name matching semantics, candidate selection
/// and business scoping are unchanged), the
/// settlement-value completed/cancelled default, historical costing through the Application
/// <see cref="ICostSale"/> use case (issue #297), and the baseline-cutoff-gated
/// <see cref="IRebuildProductCost"/> replay. An already stored transaction is only enriched
/// where it is still missing its product match or status, so an imported status or cost is never
/// overwritten - and neither is its stored instant, which is what makes re-reading the rolling
/// last-sales window unable to shift a sale a second time (issue #380).
///
/// The one identity rule that did change is where a payload item's <c>NayaxProductID</c> is resolved
/// (issue #745, parent #329 decision A2b): through the recorded provider id, by
/// <see cref="Inventory.Application.Nayax.INayaxProductLinkStore"/>, instead of by comparing it with
/// <c>Product.Id</c>. For data that predates it nothing changes - issue #732's backfill recorded
/// every product's Nayax id as the one it was keyed by, and issue #759's migration copied that onto
/// <c>Product.ExternalProductId</c> - and a Nayax id recorded on no product is an unknown product,
/// exactly as an unmatched id already was.
///
/// Issue #758 (A2c) made that answer a stored fact rather than something every later reader redoes:
/// the matched local product is written to <c>NayaxSales.ProductId</c>, the payload's own
/// <c>NayaxProductID</c> is written to <c>NayaxSales.NayaxProductId</c> and marked raw, and the
/// originating provider is recorded. The enrichment branch fills a stored sale's missing
/// association the same way - except for a sale issue #758's migration marked conflicting, which
/// only an audited decision may resolve.
///
/// The one behavioural change issue #380 made here is which payload field the stored instant comes
/// from: <c>NayaxSales.MachineAuthorizationTime</c> is a persisted true UTC instant, so it is written
/// from the authoritative <c>AuthorizationDateTimeGMT</c> value through
/// <see cref="NayaxLastSalesReport.AuthorizationInstantUtc"/>, not from the identically named but
/// machine-local <c>MachineAuthorizationTime</c> payload field. The rebuild step is per product rather than all-or-nothing (issue #362): the sales
/// are saved first and never retried, so one product's unreplayable history must not discard the
/// other products' rebuilds, and the collected failures are raised afterwards instead.
/// </summary>
public sealed class EfLatestNayaxSalesStore : ILatestNayaxSalesStore
{
    private readonly AppDbContext _db;
    private readonly ICostSale _saleCosting;
    private readonly IRebuildProductCost _inventoryCostRebuild;
    private readonly INayaxProductLinkStore _productLinks;

    public EfLatestNayaxSalesStore(
        AppDbContext db,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild,
        INayaxProductLinkStore productLinks)
    {
        _db = db;
        _saleCosting = saleCosting;
        _inventoryCostRebuild = inventoryCostRebuild;
        _productLinks = productLinks;
    }

    public async Task<LatestNayaxSalesPersistResult> PersistLatestSalesAsync(
        IReadOnlyList<NayaxLastSalesReport> sales, CancellationToken cancellationToken)
    {
        var affected = new Dictionary<long, DateTime>();
        if (sales.Count == 0)
            return new LatestNayaxSalesPersistResult(affected);

        var candidates = (await _db.Products.AsNoTracking().ToListAsync(cancellationToken))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

        // The batch's Nayax product ids are resolved to local product ids once, through the one
        // lookup over the recorded provider ids (issue #745, parent #329 decision A2b),
        // rather than compared with Product.Id - which only ever worked because the local key *is*
        // the Nayax id. Read after the catalogue read and before the per-sale work, in sequence with
        // it: both land on the request's own scoped AppDbContext, which supports one operation at a
        // time (docs/architecture.md § Concurrency inside one request).
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            sales
                .Where(sale => sale.NayaxProductId.HasValue)
                .Select(sale => sale.NayaxProductId!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);

        // The sales whose two candidate products disagreed when issue #758 resolved the existing
        // history. A conflicting sale is never resolved automatically - only an audited decision
        // names its product - so the enrichment below leaves one alone however well the fresh
        // payload would resolve. Expected to be empty: it is one tenant-filtered read of a table
        // the migration only writes to when it finds an ambiguity it must not settle.
        var conflictingSaleIds = (await _db.NayaxSaleProductConflicts.AsNoTracking()
                .Select(conflict => conflict.NayaxSaleId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var sale in sales)
        {
            var matchedProductId = ProductMatcher.MatchLinked(
                candidates,
                LocalProductId(sale.NayaxProductId, localProductIdByNayaxId),
                sale.ProductName);
            var existing = await _db.NayaxSales
                .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, cancellationToken);
            if (existing is null)
            {
                // The sale instant is the payload's authoritative AuthorizationDateTimeGMT, normalized
                // once at the integration boundary (NayaxLastSalesReport.AuthorizationInstantUtc,
                // issue #380) - never the machine-local MachineAuthorizationTime wall clock, whose
                // ticks are up to eleven hours away from the instant they would be stored as. A payload
                // item carrying no authoritative instant is not imported at a guessed time; the rolling
                // last-sales window returns the transaction again on the next refresh.
                if (sale.AuthorizationInstantUtc is not { } authorizedUtc)
                    continue;

                var added = new NayaxSales
                {
                    TransactionID = sale.TransactionID,
                    TransactionStatusId = sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250,
                    MachineID = sale.MachineID,
                    // The local product this synchronization matched, and separately the provider's
                    // own product id exactly as the payload carried it (issue #758). The column that
                    // used to hold either one now holds only the latter, and says so.
                    ProductId = matchedProductId,
                    Provider = ExternalProvider.Nayax,
                    NayaxProductId = sale.NayaxProductId,
                    ProviderProductIdIsRaw = true,
                    MachineName = sale.MachineName,
                    SettlementValue = sale.SettlementValue,
                    PaymentMethod = sale.PaymentMethod,
                    ProductName = sale.ProductName,
                    NayaxProductCostPrice = sale.ProductCostPrice,
                    MachineAuthorizationTime = authorizedUtc
                };
                _db.NayaxSales.Add(added);
                await _saleCosting.CostAsync(added, cancellationToken: cancellationToken);
                if (NayaxTransactionStatusClassifier.IsCompletedSale(added.TransactionStatusId) &&
                    matchedProductId is not null &&
                    (!affected.TryGetValue(matchedProductId.Value, out var existingAt) || added.MachineAuthorizationTime < existingAt))
                    affected[matchedProductId.Value] = added.MachineAuthorizationTime;
                continue;
            }

            var enriched = false;
            if (!existing.ProductId.HasValue && !conflictingSaleIds.Contains(existing.Id))
            {
                var existingMatch = ProductMatcher.MatchLinked(
                    candidates,
                    LocalProductId(sale.NayaxProductId, localProductIdByNayaxId),
                    sale.ProductName ?? existing.ProductName);
                if (existingMatch is not null)
                {
                    // The enrichment fills the association the stored row is still missing, and
                    // records the provider's own id beside it with its provenance (issue #758).
                    existing.ProductId = existingMatch.Value;
                    existing.Provider = ExternalProvider.Nayax;
                    existing.NayaxProductId = sale.NayaxProductId;
                    existing.ProviderProductIdIsRaw = true;
                    matchedProductId = existingMatch;
                    enriched = true;
                }
            }
            if (!existing.TransactionStatusId.HasValue)
            {
                existing.TransactionStatusId =
                    sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250;
                enriched = true;
            }
            if (enriched)
            {
                await _saleCosting.CostAsync(
                    existing,
                    cancellationToken: cancellationToken);
                if (NayaxTransactionStatusClassifier.IsCompletedSale(existing.TransactionStatusId))
                {
                    // The association persisted on the row (issue #758) - never a match re-made
                    // from the provider's product id or the reported name. A sale whose product a
                    // writer already decided replays that product; an unresolved sale replays none,
                    // which is what keeps a rename or a duplicate name out of cost history.
                    matchedProductId ??= existing.ProductId;
                    if (matchedProductId is not null &&
                        (!affected.TryGetValue(matchedProductId.Value, out var existingAt) ||
                         existing.MachineAuthorizationTime < existingAt))
                        affected[matchedProductId.Value] = existing.MachineAuthorizationTime;
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new LatestNayaxSalesPersistResult(affected);
    }

    public async Task RebuildInventoryCostsAsync(
        IReadOnlyDictionary<long, DateTime> earliestCompletedSaleByProductId,
        CancellationToken cancellationToken)
    {
        if (earliestCompletedSaleByProductId.Count == 0)
            return;

        var productIds = earliestCompletedSaleByProductId.Keys.ToList();
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => productIds.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, cancellationToken);

        // One product's unreplayable history used to discard the whole batch's staged rebuilds,
        // leaving every other product's costing position stale until its next sale, because the
        // sales themselves were already saved and are never reconsidered (issue #362). A fatal
        // rebuild now stages nothing, so the products that did rebuild are saved and the collected
        // failures are raised together afterwards - never swallowed.
        var failures = new List<string>();
        foreach (var item in earliestCompletedSaleByProductId)
        {
            if (!baselines.TryGetValue(item.Key, out var cutoff) || item.Value <= cutoff)
                continue;

            try
            {
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: cancellationToken);
            }
            catch (InventoryCostDataQualityException ex)
            {
                failures.Add($"Product {item.Key}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (failures.Count > 0)
            throw new InventoryCostDataQualityException(
                "The latest Nayax sales synchronization could not rebuild every affected product's inventory cost. " +
                string.Join(" ", failures));
    }

    /// <summary>
    /// The local product a payload item's <c>NayaxProductID</c> names, or <see langword="null"/>
    /// when this business has recorded that Nayax id on no product - an unknown product, handled exactly as an
    /// unmatched id already was: the sale is still imported, still visible and left for the name
    /// fallback and then the uncosted path, never attached to the product whose local id happens to
    /// equal the remote one (issue #745).
    /// </summary>
    private static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;
}
