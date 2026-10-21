using Inventory.Application.CatalogueReview;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// Resolving the sales a catalogue link made resolvable (issue #738): the separate, explicit, audited
/// apply that gives an unresolved stored sale its local product and replays that product's costing.
///
/// <para>It is deliberately not part of the catalogue apply. Attributing a historical sale moves
/// product-level revenue, consumes costed inventory and recosts the sales after it, which is a
/// financial decision rather than a side effect of tidying a catalogue.</para>
/// </summary>
public class LinkedProviderSaleResolutionTests : CatalogueReviewTestBase
{
    private const long ProductId = 50;
    private const long ProviderProductId = 100;

    /// <summary>
    /// The whole operation. The unresolved sales carrying the linked product's provider id are
    /// attributed to it, one audit row per sale records the rule, the actor and the instant, and the
    /// product's costing is replayed from the earliest completed sale so each one carries the cost the
    /// ledger supports rather than a guess.
    /// </summary>
    [Fact]
    public async Task Resolving_attributes_the_sales_audits_each_one_and_replays_the_product_costing()
    {
        await SeedAsync();

        LinkedProviderSaleResolution resolution;
        await using (var db = Scoped())
            resolution = await ResolveSales(db).Handle(confirmed: true, CancellationToken.None);

        var product = Assert.Single(resolution.Products);
        Assert.Equal((ProductId, "Chips", NayaxExternalId.From(ProviderProductId), 2, true),
            (product.ProductId, product.ProductName, product.ProviderProductId,
                product.ResolvedSaleCount, product.CostingReplayed));
        Assert.Equal(2, product.RecostedSaleCount);
        Assert.Equal(2, resolution.ResolvedSaleCount);

        await using var verify = Scoped();

        // Attributed and costed from the ledger: an opening position of 10 units at $2 each, so both
        // sales cost $2. Nothing here invented an opening cost and nothing read today's price.
        Assert.Equal(
            [(1L, (long?)ProductId, (decimal?)2m, (decimal?)2m, SaleCostingStatus.Costed),
             (2L, (long?)ProductId, (decimal?)2m, (decimal?)2m, SaleCostingStatus.Costed)],
            await verify.NayaxSales.AsNoTracking()
                .Where(sale => sale.TransactionID <= 2)
                .OrderBy(sale => sale.TransactionID)
                .Select(sale => ValueTuple.Create(
                    sale.TransactionID, sale.ProductId, sale.UnitCostAtSale, sale.CostOfGoodsSold,
                    sale.CostingStatus))
                .ToListAsync());

        var audit = await verify.NayaxSaleProductResolutions.AsNoTracking()
            .OrderBy(row => row.TransactionId)
            .ToListAsync();
        Assert.Equal([1L, 2L], audit.Select(row => row.TransactionId));
        Assert.All(audit, row => Assert.Equal(
            ((long?)null, ProductId, NayaxSaleProductResolutionRule.ProviderCatalogueLink,
                Operator.ToString(), AppliedAt),
            (row.PreviousProductId, row.NewProductId, row.Rule, row.DecidedBy, row.ResolvedAtUtc)));
        Assert.All(audit, row => Assert.Null(row.CandidateProductIds));

        // The product's costing position reflects the two units the replay consumed.
        var replayed = await verify.Products.AsNoTracking().SingleAsync(row => row.Id == ProductId);
        Assert.Equal(((int?)8, (decimal?)16m, 2m),
            (replayed.CostingQuantity, replayed.InventoryValue, replayed.AverageUnitCost));
    }

    /// <summary>
    /// Idempotent, because it only ever fills a null. A second run finds nothing unresolved, writes
    /// no sale, appends no audit row and replays nothing - so it cannot move a sale off the product it
    /// is already attributed to, and cannot recost history twice.
    /// </summary>
    [Fact]
    public async Task Resolving_a_second_time_changes_nothing()
    {
        await SeedAsync();

        await using (var db = Scoped())
            await ResolveSales(db).Handle(confirmed: true, CancellationToken.None);

        var before = await SnapshotAsync();

        LinkedProviderSaleResolution again;
        await using (var db = Scoped())
            again = await ResolveSales(db, AppliedAt.AddHours(1)).Handle(confirmed: true, CancellationToken.None);

        Assert.Empty(again.Products);
        Assert.Equal(0, again.ResolvedSaleCount);
        Assert.Equal(before, await SnapshotAsync());
    }

    /// <summary>
    /// A sale whose stored provider id is provenance-unknown is never resolved from it (issue #758):
    /// the value may be a raw provider id or the local product id the old synchronization wrote, so it
    /// is read as an identifier of nothing. Nor is a sale carrying some other provider's product id.
    /// </summary>
    [Fact]
    public async Task A_provenance_unknown_or_unrelated_sale_is_left_unresolved()
    {
        await SeedAsync();

        await using (var db = Scoped())
            await ResolveSales(db).Handle(confirmed: true, CancellationToken.None);

        await using var verify = Scoped();
        Assert.Equal(
            [(3L, (long?)null), (4L, (long?)null)],
            await verify.NayaxSales.AsNoTracking()
                .Where(sale => sale.TransactionID > 2)
                .OrderBy(sale => sale.TransactionID)
                .Select(sale => ValueTuple.Create(sale.TransactionID, sale.ProductId))
                .ToListAsync());
    }

    /// <summary>
    /// It is an explicit apply. Without confirmation nothing is read as a decision and nothing is
    /// written - the counts a catalogue review reports are the preview, and seeing them is not
    /// consenting to recost history.
    /// </summary>
    [Fact]
    public async Task Resolving_without_explicit_confirmation_writes_nothing()
    {
        await SeedAsync();

        await using var db = Scoped();
        await Assert.ThrowsAsync<DomainValidationException>(
            () => ResolveSales(db).Handle(confirmed: false, CancellationToken.None));

        await using var verify = Scoped();
        Assert.Empty(await verify.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
        Assert.All(
            await verify.NayaxSales.AsNoTracking().ToListAsync(), sale => Assert.Null(sale.ProductId));
    }

    /// <summary>
    /// The review is the preview: it reports, per linked product, how many unresolved sales carrying
    /// its provider id there are, and resolves none of them.
    /// </summary>
    [Fact]
    public async Task The_review_previews_the_unresolved_sales_without_resolving_any()
    {
        await SeedAsync();

        await using var db = Scoped();
        var review = await Start(db, Nayax([Product(ProviderProductId, "Chips", 3m)], []))
            .Handle(CancellationToken.None);

        // Two: the completed pair with known raw provenance. The provenance-unknown row and the row
        // carrying another provider product id are not evidence about this product.
        Assert.Equal(2, ProductEntry(review, ProviderProductId).UnresolvedSaleCount);
        Assert.All(
            await db.NayaxSales.AsNoTracking().ToListAsync(), sale => Assert.Null(sale.ProductId));
    }

    /// <summary>
    /// The seeded world: a product already linked to provider product 100, an inventory-cost
    /// transition baseline that gives its costing a known opening position, and four unresolved
    /// sales - two this operation must settle, and two it must not.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var setup = Scoped();

        setup.Products.Add(new Product
        {
            Id = ProductId,
            Name = "Chips",
            ExternalProductId = NayaxExternalId.From(ProviderProductId),
            RestockTo = 0,
            QuantityInStock = 10,
        });
        setup.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = ProductId,
            CutoffAt = Day(1),
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
            CreatedAt = Day(1),
        });
        // The third sale carries the same provider product id but with unknown provenance, so nothing
        // may read it as one; the fourth carries a provider product id this business links to nothing.
        var provenanceUnknown = Unresolved(3, ProviderProductId, Day(5));
        provenanceUnknown.ProviderProductIdIsRaw = false;

        setup.NayaxSales.AddRange(
            Unresolved(1, ProviderProductId, Day(3)),
            Unresolved(2, ProviderProductId, Day(4)),
            provenanceUnknown,
            Unresolved(4, 999, Day(6)));

        await setup.SaveChangesAsync();
    }

    private static NayaxSales Unresolved(long transactionId, long providerProductId, DateTime at) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = null,
            NayaxProductId = providerProductId,
            ProviderProductIdIsRaw = true,
            MachineAuthorizationTime = at,
            SettlementValue = 3m,
        };

    private static DateTime Day(int day) => new(2026, 9, day, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Everything a second run could change: each sale's attribution and cost, and the audit.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = Scoped();
        var sales = await db.NayaxSales.AsNoTracking().OrderBy(sale => sale.TransactionID).ToListAsync();
        var audit = await db.NayaxSaleProductResolutions.AsNoTracking()
            .OrderBy(row => row.Id).ToListAsync();
        var product = await db.Products.AsNoTracking().SingleAsync();

        return string.Join("|",
            sales.Select(sale =>
                $"S{sale.TransactionID}:{sale.ProductId}:{sale.UnitCostAtSale}:{sale.CostOfGoodsSold}:{sale.CostingStatus}:{sale.CostSource}")
            .Concat(audit.Select(row =>
                $"A{row.TransactionId}:{row.NewProductId}:{row.Rule}:{row.DecidedBy}:{row.ResolvedAtUtc:O}"))
            .Append($"P{product.Id}:{product.CostingQuantity}:{product.InventoryValue}:{product.AverageUnitCost}"));
    }
}
