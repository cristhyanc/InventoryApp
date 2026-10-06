using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

/// <summary>
/// Reads one product's configured GST rule (issue #430). <c>null</c> means the product itself is
/// not visible to the caller's business, which the tenant query filters decide; a visible product
/// with no rule answers <see cref="GstRules.None"/>.
/// </summary>
public sealed class GetProductGstRule
{
    private readonly IProductStore _store;

    public GetProductGstRule(IProductStore store)
    {
        _store = store;
    }

    public Task<GstClassification?> Handle(long id, CancellationToken cancellationToken) =>
        _store.FindGstRuleAsync(id, cancellationToken);
}
