using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Gst;

/// <summary>
/// Writes the previewed historical GST classification (issue #433), and only ever exactly that.
///
/// Everything happens in one transaction, and in this order deliberately: the authoritative read of
/// the purchase history and its configured rules, the recomputation of the plan from that read, the
/// comparison against the fingerprint the preview reported, and the write are one operation. A
/// purchase edited, added or deleted, a component classified by hand, or a product rule or supplier
/// default changed between the preview and the apply is therefore rejected rather than silently
/// applied against a history nobody approved - the same stale-preview model
/// <c>ApplyInventoryCostRepair</c> uses, and the reason the read cannot be moved outside the
/// transaction.
///
/// Nothing a caller submits is written. The request carries one fingerprint and no classification,
/// provenance, component list or GST total, and the fingerprint is only ever compared against the
/// value this method recomputes, so a tampered or foreign preview can do nothing but fail that
/// comparison. The business is resolved from the authenticated actor's membership and goes into the
/// fingerprint, so another business's preview is refused even when the two histories would otherwise
/// render alike.
///
/// What it never touches: a purchase's amounts, a line's unit cost, AVCO, costing quantity,
/// inventory value, a stock movement or a stored document. GST classification is accounting data
/// only (AGENTS.md § Purchase GST classification). A <c>Manual</c> classification is never
/// overwritten, and neither is an earlier rule-based one, which is what makes re-running Preview and
/// Apply after an Apply change nothing.
/// </summary>
public sealed class ApplyHistoricalGstClassification
{
    /// <summary>
    /// The caller-safe refusal. It asks for the one action that makes the apply safe again rather
    /// than describing what changed, because the operator cannot act on the difference and the
    /// message must not leak another actor's data.
    /// </summary>
    public const string StalePreviewMessage =
        "The purchase data or the configured GST rules changed after this preview, so nothing was " +
        "classified. Run the preview again and review it before applying.";

    private readonly IHistoricalGstClassificationStore _store;
    private readonly HistoricalGstClassificationProjection _projection;

    public ApplyHistoricalGstClassification(
        IHistoricalGstClassificationStore store,
        ICurrentBusinessProvider currentBusiness)
    {
        _store = store;
        _projection = new HistoricalGstClassificationProjection(store, currentBusiness);
    }

    public async Task<HistoricalGstClassificationApplied> Handle(
        ApplyHistoricalGstClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var projection = await _projection.BuildAsync(cancellationToken);

        // An absent or mismatched fingerprint is the same answer: this is not the plan that was
        // previewed, so nothing is written and the transaction is rolled back by its disposal.
        if (!string.Equals(projection.Preview.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
            throw new DomainValidationException(StalePreviewMessage);

        var classified = await _store.ApplyAsync(projection.Changes, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new HistoricalGstClassificationApplied(projection.Preview.Summary, classified);
    }
}
