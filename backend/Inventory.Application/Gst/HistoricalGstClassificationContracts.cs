using Inventory.Domain.Gst;

namespace Inventory.Application.Gst;

// The historical GST classification Preview/Apply contracts (issue #433), owned by the Application
// layer. They are deliberately tiny on the way in: the server derives everything from its own read,
// so a caller has nothing to supply but the preview it is confirming.

/// <summary>
/// What applying the configured rules to the unclassified purchase history would do, computed
/// without persisting anything at all - not even a draft.
///
/// <see cref="Fingerprint"/> is the value <see cref="ApplyHistoricalGstClassification"/> requires
/// back. It identifies the exact purchase data, configured rules and owning business the summary was
/// derived from, and the apply recomputes it from its own authoritative read; anything else is
/// refused.
/// </summary>
public sealed record HistoricalGstClassificationPreview(
    HistoricalGstClassificationSummary Summary,
    string Fingerprint);

/// <summary>
/// The confirmation of a preview: the fingerprint it reported, and nothing else.
///
/// There is deliberately no classification, provenance, component list, count or GST total here. The
/// apply re-derives all of them from the database inside its transaction, so no caller-supplied
/// value can influence what is written - a tampered request can only fail the fingerprint check
/// (AGENTS.md § Tenant ownership and data isolation, § Purchase GST classification).
/// </summary>
public sealed record ApplyHistoricalGstClassificationRequest(string Fingerprint);

/// <summary>
/// What the apply actually wrote: the server-recomputed summary that matched the confirmed
/// fingerprint, and how many stored components it classified.
///
/// <see cref="Summary"/> describes the state before the write, because that is the plan that was
/// applied. A fresh preview taken afterwards reports nothing left to do.
/// </summary>
public sealed record HistoricalGstClassificationApplied(
    HistoricalGstClassificationSummary Summary,
    int ComponentsClassified);
