namespace Inventory.Application.Gst;

/// <summary>
/// What happened when a caller tried to configure a GST rule (issue #430). Shared by the product
/// rule and the supplier default use cases, because the three answers are the same on both and a
/// per-slice copy could only drift.
/// </summary>
public enum GstRuleUpdateOutcome
{
    Success,
    NotFound,
    Invalid,
}
