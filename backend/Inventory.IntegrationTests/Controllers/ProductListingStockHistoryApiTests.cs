using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>GET api/products</c> - the All Products listing and the reorder listing it also serves - no
/// longer carries every product's whole stock history. Nothing that reads the listing uses it, and
/// on a real catalogue it made each search download hundreds of movements, slow enough on the
/// hosted testing environment for a search typed after a pause to overtake the previous one. The
/// history stays exactly where it is read from: a single product (<c>GET api/products/{id}</c>) and
/// the stock history endpoints.
///
/// <para>Over the same seeded business, movement and roles as
/// <see cref="StockAndPurchasingFinancialRedactionApiTests"/>, so the listing is shown to drop a
/// history that really exists, for every role.</para>
/// </summary>
public sealed class ProductListingStockHistoryApiTests
    : IClassFixture<StockAndPurchasingFinancialRedactionApiTests.ApiFactory>
{
    private const string AllProducts = "/api/products";

    private static readonly string ProductUri =
        $"/api/products/{StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ProductId.ToString(CultureInfo.InvariantCulture)}";

    private readonly StockAndPurchasingFinancialRedactionApiTests.ApiFactory _factory;

    public ProductListingStockHistoryApiTests(StockAndPurchasingFinancialRedactionApiTests.ApiFactory factory) =>
        _factory = factory;

    [Theory]
    [InlineData(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.OwnerObjectId)]
    [InlineData(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ManagerObjectId)]
    [InlineData(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.OperatorObjectId)]
    public async Task The_product_listing_carries_no_stock_history(string objectId)
    {
        await _factory.SeedAsync();

        var response = await _factory.Authenticated(objectId).GetAsync(AllProducts);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var products = json.RootElement.EnumerateArray().ToList();
        Assert.Contains(products, product =>
            product.GetProperty("id").GetInt64() == StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ProductId);
        Assert.All(products, product => Assert.False(product.TryGetProperty("stockAdjustments", out _)));
    }

    [Fact]
    public async Task A_search_of_the_product_listing_carries_no_stock_history_either()
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ManagerObjectId)
            .GetAsync($"{AllProducts}?search={Uri.EscapeDataString(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ProductName)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var product = Assert.Single(json.RootElement.EnumerateArray());
        Assert.False(product.TryGetProperty("stockAdjustments", out _));
    }

    [Fact]
    public async Task A_single_product_still_carries_its_stock_history()
    {
        await _factory.SeedAsync();

        var response = await _factory
            .Authenticated(StockAndPurchasingFinancialRedactionApiTests.ApiFactory.ManagerObjectId)
            .GetAsync(ProductUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.GetProperty("stockAdjustments").EnumerateArray());
    }
}
