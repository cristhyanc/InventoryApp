using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

/// <summary>
/// Narrow persistence port for "Set GST rule in bulk" (issue #1076), owned by the Application
/// layer.
///
/// <para>It reads and writes through the caller's business scope - the <c>AppDbContext</c> tenant
/// query filters and the ownership stamp on save - so it carries no business predicate of its own
/// and cannot reach another business's product. It is a separate port from
/// <see cref="IProductStore"/> rather than two more methods on it, because that port is the
/// product create/update/delete orchestration and nothing on it touches more than one named
/// product: a method that writes a whole catalogue's worth of rows belongs where it can be read,
/// reviewed and tested as the one thing it is.</para>
///
/// <para>Neither method takes a product id, a predicate, a column list, a filter, a page or a sort.
/// "Which products?" has exactly one answer on this port - the caller's own business's products
/// that carry no GST rule - which is what keeps the scope the operator confirms and the scope the
/// write uses from drifting apart.</para>
/// </summary>
public interface IBulkProductGstRuleStore
{
    /// <summary>
    /// How many of the caller's business's products carry no GST rule
    /// (<see cref="BulkProductGstRulePolicy.IsEligible"/>), counted over the whole catalogue -
    /// active and inactive products alike. It writes nothing.
    /// </summary>
    Task<int> CountProductsWithNoRuleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets <paramref name="rule"/> on every product of the caller's business that carries no GST
    /// rule, as <strong>one</strong> transaction: either every eligible product is configured or
    /// none is.
    ///
    /// <para>The authoritative count, the comparison against
    /// <paramref name="expectedEligibleCount"/>
    /// (<see cref="BulkProductGstRulePolicy.RequireUnchangedEligibleCount"/>) and the write are one
    /// operation, so a product configured, created or deleted between the operator's confirmation
    /// and this write refuses the whole change instead of silently extending or narrowing it. The
    /// write's own predicate is the eligibility rule again, so an explicit rule committed in
    /// between is never overwritten even where the count happens to agree.</para>
    ///
    /// <para>It writes the rule column and nothing else: no product name, price, unit cost,
    /// costing quantity, inventory value, stock movement, purchase, classification or
    /// <c>UpdatedAt</c> (AGENTS.md § Purchase GST classification).</para>
    /// </summary>
    /// <returns>How many products were configured.</returns>
    /// <exception cref="Inventory.Domain.Exceptions.DomainConflictException">
    /// The eligible count no longer matches <paramref name="expectedEligibleCount"/>. Nothing is
    /// written.
    /// </exception>
    Task<int> SetRuleOnProductsWithNoRuleAsync(
        GstClassification rule, int expectedEligibleCount, CancellationToken cancellationToken);
}
