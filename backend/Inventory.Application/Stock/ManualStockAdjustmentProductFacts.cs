namespace Inventory.Application.Stock;

/// <summary>
/// The product facts a manual stock adjustment needs before it validates the request (issue #1249):
/// its name, to refuse a retired product by, and whether it is active, to decide whether to.
/// </summary>
public sealed record ManualStockAdjustmentProductFacts(string Name, bool IsActive);
