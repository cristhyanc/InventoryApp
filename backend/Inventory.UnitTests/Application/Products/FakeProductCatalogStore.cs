using Inventory.Application.Products;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// In-memory fake of the product catalogue read port, so Application read use-case tests exercise
/// orchestration without depending on EF Core or SQLite. The filtering and by-name ordering it
/// reproduces are the adapter's own concern and are covered relationally by
/// <c>ProductServiceTests</c>/<c>EfProductCatalogStoreTenancyTests</c>; what matters here is which
/// method each use case calls and what it does with the rows.
/// </summary>
public sealed class FakeProductCatalogStore : IProductCatalogStore
{
    private readonly List<ProductRecord> _products;

    public FakeProductCatalogStore(params ProductRecord[] products)
    {
        _products = [.. products];
    }

    public ProductCatalogFilter? LastFilter { get; private set; }
    public int ListOrderedByNameCalls { get; private set; }
    public int ListUnorderedCalls { get; private set; }

    public Task<IReadOnlyList<ProductRecord>> ListOrderedByNameAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken)
    {
        ListOrderedByNameCalls++;
        LastFilter = filter;
        return Task.FromResult<IReadOnlyList<ProductRecord>>(
            [.. Matching(filter).OrderBy(product => product.Name)]);
    }

    public Task<IReadOnlyList<ProductRecord>> ListUnorderedAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken)
    {
        ListUnorderedCalls++;
        LastFilter = filter;
        return Task.FromResult<IReadOnlyList<ProductRecord>>([.. Matching(filter)]);
    }

    public Task<ProductRecord?> FindAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_products.FirstOrDefault(product => product.Id == id));

    private IEnumerable<ProductRecord> Matching(ProductCatalogFilter filter)
    {
        var products = _products.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
            products = products.Where(product => product.Name.Contains(filter.Search, StringComparison.Ordinal)
                || (product.Sku is not null && product.Sku.Contains(filter.Search, StringComparison.Ordinal)));

        if (filter.CategoryId.HasValue)
            products = products.Where(product => product.CategoryId == filter.CategoryId);
        if (filter.SupplierId.HasValue)
            products = products.Where(product => product.SupplierId == filter.SupplierId);

        return products;
    }

    /// <summary>A minimal valid catalogue product, with only the fields a test cares about set.</summary>
    public static ProductRecord Product(
        long id,
        string name,
        int quantityInStock = 0,
        int lowStockThreshold = 0,
        int restockTo = 0,
        decimal averageUnitCost = 0m,
        bool isActive = true,
        string? sku = null) => new()
        {
            Id = id,
            Name = name,
            Sku = sku,
            UnitPrice = 0m,
            AverageUnitCost = averageUnitCost,
            QuantityInStock = quantityInStock,
            LowStockThreshold = lowStockThreshold,
            RestockTo = restockTo,
            IsActive = isActive,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
}
