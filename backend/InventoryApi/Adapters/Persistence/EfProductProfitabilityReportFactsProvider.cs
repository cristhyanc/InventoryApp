using Inventory.Application.Reporting.ProductProfitability;
using InventoryApi.Data;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IProductProfitabilityReportFactsProvider"/>. It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on
/// <see cref="AppDbContext"/> and persistence models that still live in InventoryApi. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
///
/// Its completed-sale query is shared with <see cref="EfBookkeepingReportFactsProvider"/> and
/// <see cref="EfMachineProfitabilityReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// This adapter returns only raw sale groups (keyed by the raw Nayax product identifier/name, as
/// query mechanics) and the catalogue candidates; it deliberately does not call the product
/// matcher itself, since deterministic product matching is Domain business logic
/// (<c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c>), not a persistence concern,
/// and is applied by <see cref="GetProductProfitabilityReport"/>. Card/cash classification is not
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
            .Select(x => new { x.NayaxProductId, x.ProductName, x.SettlementValue, x.CostOfGoodsSold, x.PaymentMethod })
            .ToListAsync(cancellationToken);

        var saleGroups = saleRows
            .GroupBy(x => new { x.NayaxProductId, x.ProductName })
            .Select(g => new ProductProfitabilitySaleGroupFacts(
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

        return new ProductProfitabilityReportFacts(saleGroups, catalogue);
    }
}
