using System.Net;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Infrastructure.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// <see cref="ImportNayaxProductCatalog"/> (issue #300) orchestrates the Nayax product catalogue
/// import that used to live in <c>ImportService.ImportProductsAsync</c>: read the operator's
/// products and product groups from the <see cref="INayaxLynxClient"/> port, project them onto the
/// catalogue fields the import owns, and hand the whole snapshot to
/// <see cref="INayaxProductCatalogImportStore"/> in one call.
///
/// The projection is where <c>Product.UnitPrice</c>'s Nayax-managed semantics live (AGENTS.md
/// § Inventory and historical costing invariants): the catalogue <c>ProductDefaultRetailPrice</c> is
/// the selling price, and the Nayax <c>ProductCostPrice</c> must never reach it.
/// </summary>
public class ImportNayaxProductCatalogTests
{
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 2, 15, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_remote_catalogue_is_projected_onto_the_import_snapshot()
    {
        var store = new RecordingStore();

        await Import(
            store,
            products:
            [
                new NayaxProduct
                {
                    NayaxProductId = 100,
                    ProductName = "Chips",
                    ProductDescription = "Salted",
                    ProductDefaultRetailPrice = 3.50m,
                    ProductGroupId = 10,
                },
            ],
            groups: [new NayaxProductGroup { ProductGroupID = 10, ProductGroupName = "Snacks" }]);

        var import = Assert.Single(store.Applied);
        Assert.Equal(ImportedAt, import.ImportedAtUtc);
        Assert.Equal([new ImportedProductCategory(10, "Snacks", "Snacks")], import.Categories);
        Assert.Equal(
            [new ImportedProductCatalogEntry(100, "Chips", "Salted", 3.50m, 10)],
            import.Products);
    }

    /// <summary>
    /// The selling price comes from the catalogue default retail price (<c>ProductDefaultRetailPrice</c>,
    /// confirmed per issue #363), never from the Nayax cost field (issue #57). A product Nayax
    /// prices at nothing imports as 0, exactly as before.
    /// </summary>
    [Fact]
    public async Task Unit_price_comes_from_the_default_retail_price_and_a_missing_price_imports_as_zero()
    {
        var store = new RecordingStore();

        await Import(
            store,
            products:
            [
                new NayaxProduct { NayaxProductId = 100, ProductName = "Priced", ProductCostPrice = 1.10m, ProductDefaultRetailPrice = 3.50m },
                new NayaxProduct { NayaxProductId = 101, ProductName = "Unpriced", ProductCostPrice = 2.20m },
            ],
            groups: []);

        var import = Assert.Single(store.Applied);
        Assert.Equal([3.50m, 0m], import.Products.Select(product => product.UnitPrice));
    }

    /// <summary>
    /// A group with no identifier, or with no usable name, is not a category this import can
    /// create, exactly as the former implementation's two guards decided.
    /// </summary>
    [Theory]
    [InlineData(null, "Snacks")]
    [InlineData(10, null)]
    [InlineData(10, "")]
    [InlineData(10, "   ")]
    public async Task A_group_without_an_id_or_a_usable_name_becomes_no_category(int? groupId, string? groupName)
    {
        var store = new RecordingStore();

        await Import(
            store,
            products: [],
            groups: [new NayaxProductGroup { ProductGroupID = groupId, ProductGroupName = groupName }]);

        Assert.Empty(Assert.Single(store.Applied).Categories);
    }

    /// <summary>
    /// Partial remote data must not reach persistence: a failed Nayax read propagates and nothing
    /// is applied, so a catalogue the operator never returned cannot overwrite local products.
    /// </summary>
    [Fact]
    public async Task A_failed_Nayax_read_propagates_and_applies_nothing()
    {
        var store = new RecordingStore();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxUpstreamException(
                "GetProducts", HttpMethod.Get, "operators/1/products", HttpStatusCode.BadGateway));
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var useCase = new ImportNayaxProductCatalog(nayax.Object, store, new FixedClock(ImportedAt));

        await Assert.ThrowsAsync<NayaxUpstreamException>(() => useCase.Handle(CancellationToken.None));
        Assert.Empty(store.Applied);
    }

    [Fact]
    public async Task The_request_cancellation_token_reaches_both_Nayax_reads()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingStore();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(cancellation.Token)).ReturnsAsync([]);
        nayax.Setup(client => client.GetProductGroupssAsync(cancellation.Token)).ReturnsAsync([]);

        await new ImportNayaxProductCatalog(nayax.Object, store, new FixedClock(ImportedAt))
            .Handle(cancellation.Token);

        nayax.Verify(client => client.GetProductsAsync(cancellation.Token), Times.Once);
        nayax.Verify(client => client.GetProductGroupssAsync(cancellation.Token), Times.Once);
    }

    private static Task Import(
        INayaxProductCatalogImportStore store,
        List<NayaxProduct> products,
        List<NayaxProductGroup> groups)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(products);
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>())).ReturnsAsync(groups);

        return new ImportNayaxProductCatalog(nayax.Object, store, new FixedClock(ImportedAt))
            .Handle(CancellationToken.None);
    }

    private sealed class RecordingStore : INayaxProductCatalogImportStore
    {
        public List<NayaxProductCatalogImport> Applied { get; } = [];

        public Task ApplyAsync(NayaxProductCatalogImport import, CancellationToken cancellationToken)
        {
            Applied.Add(import);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; }
    }
}
