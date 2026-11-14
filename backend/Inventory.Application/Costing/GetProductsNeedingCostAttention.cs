namespace Inventory.Application.Costing;

/// <summary>
/// One product whose inventory-cost replay was attempted and failed, and is still waiting to be
/// replayed (issue #1103's durable pending rebuild, read for the Dashboard).
/// </summary>
/// <param name="ProductId">The business's own product id.</param>
/// <param name="ProductName">The product's current name, or <see langword="null"/> when no product with that id is stored.</param>
/// <param name="FailureCode">
/// The short failure code the last attempt recorded: <c>data_quality</c> when the product's history
/// has a fatal costing issue (for example a positive restock with no unit cost), or
/// <c>product_missing</c> when the product could not be loaded.
/// </param>
/// <param name="AttemptCount">How many replays of this work have failed so far.</param>
/// <param name="LastAttemptAtUtc">When the last failed replay ran, as a UTC instant.</param>
public sealed record ProductNeedingCostAttention(
    long ProductId,
    string? ProductName,
    string FailureCode,
    int AttemptCount,
    DateTime? LastAttemptAtUtc);

/// <summary>
/// Read port for the products whose pending inventory-cost rebuild has failed at least once and is
/// still pending. It reads the caller's own business only, through the central tenant query filter.
/// </summary>
public interface IProductCostAttentionReader
{
    /// <summary>
    /// Every product of this business whose last inventory-cost replay failed and whose rebuild is
    /// still pending, oldest failure first. Empty when nothing is waiting on a failed replay.
    /// </summary>
    Task<IReadOnlyList<ProductNeedingCostAttention>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Dashboard's "costs need attention" read.
///
/// <para>A product whose cost history cannot be replayed no longer fails the Nayax sales
/// synchronization: the sales are stored either way, and one product's missing purchase used to stop
/// the whole business's sync on every visit, hiding every other fresh figure behind a "could not be
/// synchronized" warning. The failure is still never swallowed - it stays a durable pending rebuild
/// that every later run retries, and this read is how it is shown, by product, to the people who can
/// fix the product's history.</para>
/// </summary>
public sealed class GetProductsNeedingCostAttention
{
    private readonly IProductCostAttentionReader _reader;

    public GetProductsNeedingCostAttention(IProductCostAttentionReader reader)
    {
        _reader = reader;
    }

    public Task<IReadOnlyList<ProductNeedingCostAttention>> Handle(CancellationToken cancellationToken) =>
        _reader.ListAsync(cancellationToken);
}
