namespace Inventory.Domain.Expenses;

/// <summary>
/// The deterministic validation rules for an operating expense's descriptive and financial
/// fields, shared by create and update. Amounts are not cross-checked against each other - GST
/// semantics stay a caller concern - this only guards the invariants the controller enforced
/// before this slice moved into Domain/Application.
/// </summary>
public sealed class OperatingExpenseDetails
{
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
