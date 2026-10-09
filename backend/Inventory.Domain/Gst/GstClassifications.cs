namespace Inventory.Domain.Gst;

/// <summary>
/// The supported <see cref="GstClassification"/> vocabulary, and the check every value that crosses
/// a transport boundary has to pass before it is persisted or calculated with.
///
/// A C# enum is only a compile-time constraint. A multipart form field and a deserialized JSON
/// property both bind an arbitrary integer - <c>999</c>, <c>-1</c> - to a
/// <see cref="GstClassification"/>, and such a value is not "unknown": it is a state no rule
/// describes. Left alone it would be persisted with a provenance that claims a person chose it, and
/// <c>PurchaseGstPolicy.Calculate</c> would quietly treat it as zero GST without ever reporting it
/// as unresolved - exactly the inference AGENTS.md § Purchase GST classification forbids. So it is
/// rejected instead, at the boundary.
/// </summary>
public static class GstClassifications
{
    /// <summary>
    /// The message an unsupported classification is rejected with. Built from the enum's own member
    /// names so it cannot drift from the vocabulary it describes.
    /// </summary>
    public static string UnsupportedMessage { get; } =
        $"A GST classification must be one of {string.Join(", ", Enum.GetNames<GstClassification>())}.";

    /// <summary>Whether <paramref name="classification"/> is one of the declared classifications.</summary>
    public static bool IsSupported(GstClassification classification) => Enum.IsDefined(classification);

    /// <summary>
    /// Whether a submitted classification is supported or omitted altogether. An omitted value is
    /// always valid: it means "not submitted", which keeps a stored classification on an edit and
    /// stays unclassified on a create.
    /// </summary>
    public static bool IsSupportedOrOmitted(GstClassification? classification) =>
        classification is null || IsSupported(classification.Value);

    /// <summary>
    /// <paramref name="classification"/> itself when it is supported. This is the deliberate
    /// fail-closed guard inside the Domain: a classification that reached a calculation without
    /// being validated at the boundary throws rather than contributing a silent zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="classification"/> is not a declared value.</exception>
    public static GstClassification Require(GstClassification classification) =>
        IsSupported(classification)
            ? classification
            : throw new ArgumentOutOfRangeException(nameof(classification), classification, UnsupportedMessage);
}
