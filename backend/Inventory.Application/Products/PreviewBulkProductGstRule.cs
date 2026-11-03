namespace Inventory.Application.Products;

/// <summary>
/// Counts the products of the caller's business that carry no GST rule (issue #1076), so the
/// operator can see the scope of a bulk change and confirm it before anything is written.
///
/// <para>It persists nothing at all, not even a draft, and it takes no parameter: no product, no
/// filter, no page and no business, which is the caller's own resolved from their membership
/// through the tenant query filters. The count it answers is the concurrency token the apply
/// requires back (<see cref="SetBulkProductGstRule"/>), which is why it is as restricted as the
/// apply and why it is a <c>POST</c> at the HTTP boundary - the same reasoning the historical GST
/// classification preview records.</para>
/// </summary>
public sealed class PreviewBulkProductGstRule
{
    private readonly IBulkProductGstRuleStore _store;

    public PreviewBulkProductGstRule(IBulkProductGstRuleStore store)
    {
        _store = store;
    }

    public async Task<BulkProductGstRulePreview> Handle(CancellationToken cancellationToken = default)
    {
        var eligibleCount = await _store
            .CountProductsWithNoRuleAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BulkProductGstRulePreview(eligibleCount);
    }
}
