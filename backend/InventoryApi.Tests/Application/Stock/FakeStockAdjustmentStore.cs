using Inventory.Application.Stock;
using Inventory.Domain.Stock;

namespace InventoryApi.Tests.Application.Stock;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite. See <c>EfStockAdjustmentStoreTests</c> for behaviour
/// that needs a real transaction/query.
/// </summary>
public sealed class FakeStockAdjustmentStore : IStockAdjustmentStore
{
    private int _nextId = 1;

    public bool ProductExists { get; set; } = true;
    public IReadOnlyList<StockAdjustmentRecord> History { get; set; } = Array.Empty<StockAdjustmentRecord>();
    public RestockCostFacts? RestockCostFacts { get; set; }
    public Exception? ThrowOnApply { get; set; }
    public bool ListHistoryCalled { get; private set; }
    public bool ApplyCalled { get; private set; }
    public ManualStockAdjustmentInput? LastAppliedInput { get; private set; }

    public Task<bool> ProductExistsAsync(long productId, CancellationToken cancellationToken) => Task.FromResult(ProductExists);

    public Task<IReadOnlyList<StockAdjustmentRecord>> ListHistoryAsync(long productId, CancellationToken cancellationToken)
    {
        ListHistoryCalled = true;
        return Task.FromResult(History);
    }

    public Task<RestockCostFacts?> GetRestockCostFactsAsync(long productId, CancellationToken cancellationToken) =>
        Task.FromResult(RestockCostFacts);

    public Task<StockAdjustmentRecord> ApplyAsync(long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken)
    {
        ApplyCalled = true;
        LastAppliedInput = input;

        if (ThrowOnApply is not null) throw ThrowOnApply;

        var record = new StockAdjustmentRecord(
            _nextId++, 1, productId, null, input.QuantityChange, input.QuantityChange,
            input.UnitCost, null, null, null, null,
            input.Reason, StockAdjustmentSource.Manual, input.MachineId, input.Notes, input.EatBefore,
            DateTime.UtcNow, DateTime.UtcNow);
        return Task.FromResult(record);
    }
}
