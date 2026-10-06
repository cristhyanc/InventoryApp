namespace Inventory.Domain.Gst;

/// <summary>
/// One component's GST classification together with how it was established. The two always travel
/// together: a classification without its provenance cannot be audited, and a provenance without a
/// classification means nothing.
/// </summary>
public readonly record struct GstClassificationState(
    GstClassification Classification,
    GstClassificationSource Source)
{
    /// <summary>
    /// The state of a component nothing is known about: unclassified, with no provenance. It is the
    /// default for a new component, for every row the additive GST migration touches, and for any
    /// charge that is absent altogether.
    /// </summary>
    public static GstClassificationState Unclassified => default;
}
