using Inventory.Application.Tenancy;

namespace Inventory.Application.Gst;

/// <summary>
/// Previews classifying the unclassified purchase history from the configured product GST rules and
/// supplier GST defaults (issue #433), without persisting anything at all - not even a draft.
///
/// The preview is where the maintenance action is decided: it reports how many components were
/// examined, how many would become taxable, how many GST-free and how many stay unclassified, the
/// charge counts separately for delivery and package, and the input GST the change would make
/// available. It also reports the fingerprint of the purchase data, rules and business it was
/// computed from, which <see cref="ApplyHistoricalGstClassification"/> requires back.
///
/// It is read-only by construction: it holds no transaction and no write path, and the only store
/// method it reaches is the load.
/// </summary>
public sealed class PreviewHistoricalGstClassification
{
    private readonly HistoricalGstClassificationProjection _projection;

    public PreviewHistoricalGstClassification(
        IHistoricalGstClassificationStore store,
        ICurrentBusinessProvider currentBusiness) =>
        _projection = new HistoricalGstClassificationProjection(store, currentBusiness);

    public async Task<HistoricalGstClassificationPreview> Handle(CancellationToken cancellationToken = default) =>
        (await _projection.BuildAsync(cancellationToken)).Preview;
}
