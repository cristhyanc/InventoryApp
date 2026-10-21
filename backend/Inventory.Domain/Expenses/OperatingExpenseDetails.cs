namespace Inventory.Domain.Expenses;

/// <summary>
/// The deterministic validation rules for an operating expense's descriptive and financial
/// fields, shared by create and update. Amounts are not cross-checked against each other - GST
/// semantics stay a caller concern - this only guards the invariants the controller enforced
/// before this slice moved into Domain/Application.
/// </summary>
public sealed class OperatingExpenseDetails
{
    /// <summary>
    /// What a caller is told when the expense names a machine that is not one of this business's
    /// machines (issue #780). The rule itself is not deterministic over the submitted fields - it
    /// needs the business's machines - so it is applied by the create/update use cases; the message
    /// lives here because this is where an operating expense's refusals are written, and because
    /// both use cases have to give the same one. It names no machine and no business.
    /// </summary>
    public const string UnknownMachineMessage =
        "The selected machine is not a machine of this business.";

    public const string DescriptionRequiredMessage = "Description is required.";
    public const string NegativeAmountsMessage = "Expense amounts cannot be negative.";
    public const string ServicePeriodOrderMessage = "Service period end must not be before its start.";

    public string Description { get; }

    private OperatingExpenseDetails(string description)
    {
        Description = description;
    }

    public static bool TryCreate(
        string description,
        decimal amountExGst,
        decimal gstAmount,
        decimal totalAmount,
        DateTime? servicePeriodStart,
        DateTime? servicePeriodEnd,
        out OperatingExpenseDetails? details,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            details = null;
            error = DescriptionRequiredMessage;
            return false;
        }

        if (amountExGst < 0 || gstAmount < 0 || totalAmount < 0)
        {
            details = null;
            error = NegativeAmountsMessage;
            return false;
        }

        if (servicePeriodStart.HasValue && servicePeriodEnd.HasValue && servicePeriodEnd < servicePeriodStart)
        {
            details = null;
            error = ServicePeriodOrderMessage;
            return false;
        }

        details = new OperatingExpenseDetails(description.Trim());
        error = null;
        return true;
    }
}
