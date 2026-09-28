using Inventory.Application.Purchases;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// In-memory fake of the product purchase-price-history port, so the use case's composition with
/// the Domain policy can be tested without EF Core or SQLite.
/// </summary>
public sealed class FakeProductPurchasePriceHistoryProvider : IProductPurchasePriceHistoryProvider
{
    private readonly IReadOnlyList<SupplierPriceHistoryFact> _facts;

    public FakeProductPurchasePriceHistoryProvider(IReadOnlyList<SupplierPriceHistoryFact> facts) => _facts = facts;

    public Task<IReadOnlyList<SupplierPriceHistoryFact>> GetForProductAsync(long productId, CancellationToken cancellationToken) =>
        Task.FromResult(_facts);
}
