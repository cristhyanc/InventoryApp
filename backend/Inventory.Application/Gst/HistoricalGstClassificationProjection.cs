using Inventory.Application.Tenancy;
using Inventory.Domain.Gst;

namespace Inventory.Application.Gst;

/// <summary>
/// The preview a historical GST classification would produce, plus the component writes it stands
/// for, so the apply can persist exactly the plan it validated.
/// </summary>
internal sealed record HistoricalGstClassificationProjectionResult(
    HistoricalGstClassificationPreview Preview,
    IReadOnlyList<HistoricalGstClassificationChange> Changes);

/// <summary>
/// Projects the configured product rules and supplier defaults onto one business's unclassified
/// purchase history (issue #433), shared by <see cref="PreviewHistoricalGstClassification"/> and
/// <see cref="ApplyHistoricalGstClassification"/> so the numbers an operator approves and the
/// numbers the apply validates come from one calculation over one read.
///
/// It persists nothing. The order here is deliberate: the business is resolved from the
/// authenticated actor's membership first, so an unresolved caller reads nothing at all rather than
/// projecting an unscoped history, and the resolved id then goes into the fingerprint, which is what
/// binds a preview to its tenant. No business id is ever accepted from the request.
/// </summary>
internal sealed class HistoricalGstClassificationProjection
{
    private readonly IHistoricalGstClassificationStore _store;
    private readonly ICurrentBusinessProvider _currentBusiness;

    public HistoricalGstClassificationProjection(
        IHistoricalGstClassificationStore store,
        ICurrentBusinessProvider currentBusiness)
    {
        _store = store;
        _currentBusiness = currentBusiness;
    }

    public async Task<HistoricalGstClassificationProjectionResult> BuildAsync(CancellationToken cancellationToken)
    {
        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken);
        var purchases = await _store.LoadPurchasesAsync(cancellationToken);
        var plan = HistoricalGstClassificationPolicy.Plan(purchases);
        var fingerprint = HistoricalGstClassificationFingerprint.Compute(businessId.Value, purchases);

        return new HistoricalGstClassificationProjectionResult(
            new HistoricalGstClassificationPreview(plan.Summary, fingerprint),
            plan.Changes);
    }
}
