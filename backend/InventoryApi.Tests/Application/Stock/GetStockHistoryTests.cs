using Inventory.Application.Stock;
using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Application.Stock;

public class GetStockHistoryTests
{
    [Fact]
    public async Task An_unknown_product_returns_an_empty_history_without_querying_the_store()
    {
        var store = new FakeStockAdjustmentStore { ProductExists = false };
        var useCase = new GetStockHistory(store);

        var history = await useCase.Handle(1, CancellationToken.None);

        Assert.Empty(history);
        Assert.False(store.ListHistoryCalled);
    }

    [Fact]
    public async Task A_known_product_returns_the_stores_history()
    {
        var record = new StockAdjustmentRecord(
            1, 1, 1, null, 4, 4, 1.5m, 6m, null, null, null,
            StockAdjustmentReason.Restock, StockAdjustmentSource.Manual, null, null, null,
            DateTime.UtcNow, DateTime.UtcNow);
        var store = new FakeStockAdjustmentStore { History = [record] };
        var useCase = new GetStockHistory(store);

        var history = await useCase.Handle(1, CancellationToken.None);

        Assert.Same(record, Assert.Single(history));
    }
}
