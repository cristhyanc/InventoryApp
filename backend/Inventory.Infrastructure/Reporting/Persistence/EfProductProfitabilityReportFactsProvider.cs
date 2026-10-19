using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductProfitabilityReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure with the <see cref="AppDbContext"/> and persistence models it depends on
/// (issue #307 moved those, issue #308 this adapter family).
///
/// Its completed-sale query is shared with <see cref="EfBookkeepingReportFactsProvider"/> and
/// <see cref="EfMachineProfitabilityReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// This adapter returns sale groups keyed by the local product each sale is attributed to (issue
/// #758, parent #329 decision A2c), plus the catalogue. It no longer matches anything: the
/// association is persisted on the sale by the writer that stored it, so a product renamed or
/// duplicated after the sale cannot move the sale's revenue and COGS between rows. Sales with no
/// association group by the identity the provider reported and are presented as unresolved by
/// <see cref="GetProductProfitabilityReport"/>, never folded into a catalogue product and never
/// dropped from the totals. Card/cash classification is not
/// EF-translatable, so this adapter materializes only the required sale columns and
/// classifies/groups them in memory, matching the pattern already used by
/// <see cref="EfBookkeepingReportFactsProvider"/>.
/// </summary>
public sealed class EfProductProfitabilityReportFactsProvider : IProductProfitabilityReportFactsProvider
{
    private readonly AppDbContext _db;

    public EfProductProfitabilityReportFactsProvider(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ProductProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);

        var catalogue = await _db.Products.AsNoTracking()
            .Select(product => new ProductProfitabilityCatalogueEntry(
                product.Id, product.Name, product.Category == null ? null : product.Category.Name))
            .ToListAsync(cancellationToken);

        var saleRows = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .Select(x => new
            {
                x.ProductId,
                x.NayaxProductId,
                x.ProductName,
                x.SettlementValue,
                x.CostOfGoodsSold,
                x.PaymentMethod,
            })
            .ToListAsync(cancellationToken);

        // Grouped by the local product the sale is attributed to (issue #758). An unresolved sale
        // has none, so it groups with the other unresolved sales that carry the same reported
        // identity - which is a presentation grouping of rows nothing could attribute, never an
        // attribution to a catalogue product.
        var saleGroups = saleRows
            .GroupBy(x => new { x.ProductId, NayaxProductId = x.ProductId is null ? x.NayaxProductId : null, ProductName = x.ProductId is null ? x.ProductName : null })
            .Select(g => new ProductProfitabilitySaleGroupFacts(
                g.Key.ProductId,
                g.Key.NayaxProductId,
                g.Key.ProductName,
                g.Sum(x => x.SettlementValue),
                g.Count(),
                g.Sum(x => x.CostOfGoodsSold ?? 0m),
                g.All(x => x.CostOfGoodsSold.HasValue),
                g.Count(x => !x.CostOfGoodsSold.HasValue),
                g.Where(x => !x.CostOfGoodsSold.HasValue).Sum(x => x.SettlementValue),
                g.Count(),
                g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue),
                g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue)))
            .ToList();

        // Every stored sale in scope, over every status and before the completed-sale filter
        // (issue #731): a period holding only cancelled rows has sales data and no product rows at
        // all, which is unverified rather than a measured "nothing sold".
        var containsSalesRows = await EfReportingSharedQueries
            .AllSalesQuery(_db, from, endExclusive, machineId).AnyAsync(cancellationToken);
        var coverage = await EfReportingSharedQueries.CoverageFactsAsync(
            _db, from, endExclusive, containsSalesRows, containsSettlementRows: false, cancellationToken);

        return new ProductProfitabilityReportFacts(saleGroups, catalogue)
        {
            Coverage = coverage
        };
    }
}
