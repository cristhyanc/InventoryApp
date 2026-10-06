namespace Inventory.Application.Gst;

/// <summary>
/// The result of configuring a GST rule: the outcome, plus the message an invalid submission is
/// refused with. Validation is reported rather than thrown, the shape
/// <c>Inventory.Application.Products.UpdateProductResult</c> established (issue #303), so the
/// controller maps it to <c>400</c> without a broad exception catch that would also convert an
/// unexpected failure from below the use case into a client error.
/// </summary>
public sealed record GstRuleUpdateResult(GstRuleUpdateOutcome Outcome, string? ValidationError)
{
    public static GstRuleUpdateResult Success() => new(GstRuleUpdateOutcome.Success, null);

    public static GstRuleUpdateResult NotFound() => new(GstRuleUpdateOutcome.NotFound, null);

    public static GstRuleUpdateResult Invalid(string error) => new(GstRuleUpdateOutcome.Invalid, error);
}
