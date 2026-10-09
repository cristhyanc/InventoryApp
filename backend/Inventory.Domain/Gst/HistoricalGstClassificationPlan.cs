namespace Inventory.Domain.Gst;

/// <summary>
/// One component the historical classification Apply would write (issue #433). It names the stored
/// component and the state to persist on it, and nothing else: the rule that produced it has already
/// been applied, and no amount, total or GST figure travels with it, so the write cannot disagree
/// with the calculation that approved it.
///
/// <see cref="LineId"/> identifies the purchase line for <see cref="GstComponentKind.ProductLine"/>
/// and is <c>null</c> for a delivery or package charge, which belongs to the purchase itself.
/// </summary>
public readonly record struct HistoricalGstClassificationChange(
    GstComponentKind Kind,
    int PurchaseId,
    int? LineId,
    GstClassification Classification,
    GstClassificationSource Source);

/// <summary>
/// How many components of one kind the preview examined and what would become of them. The three
/// outcomes are exhaustive and deliberately kept apart: a component no rule covers stays
/// <see cref="GstClassification.Unknown"/>, which is a reported outcome rather than a silent
/// omission.
/// </summary>
public readonly record struct HistoricalGstComponentCounts(
    int Examined,
    int BecomingTaxable,
    int BecomingGstFree,
    int StayingUnknown);

/// <summary>
/// What applying the configured rules to the unclassified purchase history would do, as the Preview
/// reports it and the Apply re-derives it (issue #433).
///
/// The per-kind counts are separate because the acceptance criteria ask for the charge counts by
/// type, and because a delivery charge and a package charge are classified by different configured
/// defaults. <see cref="LineGst"/> and <see cref="ChargeGst"/> come from
/// <c>Inventory.Domain.Reporting.Gst.PurchaseInputGstPolicy</c> over the newly classified components
/// only, so <see cref="InputGst"/> is the input GST this maintenance action would make available -
/// not the business's whole period GST.
/// </summary>
public sealed record HistoricalGstClassificationSummary(
    HistoricalGstComponentCounts ProductLines,
    HistoricalGstComponentCounts DeliveryCharges,
    HistoricalGstComponentCounts PackageCharges,
    int PurchasesExamined,
    decimal LineGst,
    decimal ChargeGst,
    decimal StayingUnknownAmount)
{
    /// <summary>Nothing examined: what an already fully classified history projects to.</summary>
    public static HistoricalGstClassificationSummary Empty { get; } =
        new(default, default, default, 0, 0m, 0m, 0m);

    /// <summary>Every unclassified component the preview examined, across the three kinds.</summary>
    public int ComponentsExamined =>
        ProductLines.Examined + DeliveryCharges.Examined + PackageCharges.Examined;

    /// <summary>Components a rule would classify as taxable.</summary>
    public int BecomingTaxable =>
        ProductLines.BecomingTaxable + DeliveryCharges.BecomingTaxable + PackageCharges.BecomingTaxable;

    /// <summary>Components a rule would classify as GST-free.</summary>
    public int BecomingGstFree =>
        ProductLines.BecomingGstFree + DeliveryCharges.BecomingGstFree + PackageCharges.BecomingGstFree;

    /// <summary>Components no configured rule covers, which stay unclassified and unresolved.</summary>
    public int StayingUnknown =>
        ProductLines.StayingUnknown + DeliveryCharges.StayingUnknown + PackageCharges.StayingUnknown;

    /// <summary>The input GST the newly classified components would contribute.</summary>
    public decimal InputGst => LineGst + ChargeGst;

    /// <summary>Whether applying this plan would classify anything at all.</summary>
    public bool ClassifiesAnything => BecomingTaxable + BecomingGstFree > 0;
}

/// <summary>
/// The summary a person approves and the exact component writes it stands for (issue #433). The two
/// always travel together, and only the summary ever leaves the server: the Apply recomputes this
/// whole plan from its own authoritative read, so the changes are never submitted by a caller.
/// </summary>
public sealed record HistoricalGstClassificationPlan(
    HistoricalGstClassificationSummary Summary,
    IReadOnlyList<HistoricalGstClassificationChange> Changes);
