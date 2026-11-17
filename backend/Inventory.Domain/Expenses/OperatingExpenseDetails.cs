namespace Inventory.Domain.Expenses;

/// <summary>
/// The deterministic validation rules for an operating expense's descriptive and financial
/// fields, shared by create and update: the invariants the controller enforced before this slice
/// moved into Domain/Application, plus the rule of issue #1257 that the three amounts describe one
/// expense.
///
/// <para><strong>Since issue #1257 the amounts are cross-checked.</strong> Profit reads the total
/// and the GST credit reads the GST, so an expense whose ex-GST and GST do not add up to its total
/// states two different costs at once - ex GST 100, GST 50, total 110 used to be accepted and
/// reported both. The tolerance is one cent, which is the rounding a supplier's own document
/// legitimately carries; anything wider is a typo and is refused rather than reported. The rule is
/// deliberately only a cross-check: nothing is calculated, rounded or filled in for the caller, so
/// the amounts stored are still the amounts on the document.</para>
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

    /// <summary>
    /// What a caller is told when the three amounts do not describe one expense (issue #1257).
    /// </summary>
    public const string AmountsMustAddUpMessage =
        "Amount ex GST plus GST must equal the total, within $0.01.";

    public const string ServicePeriodOrderMessage = "Service period end must not be before its start.";

    /// <summary>
    /// How far ex GST plus GST may sit from the total and still be the same expense (issue #1257):
    /// one cent, the same tolerance a reimbursement period is reconciled to. A difference of
    /// exactly this much is accepted and a cent more is refused.
    /// </summary>
    public const decimal AmountTolerance = 0.01m;

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

        // Issue #1257. Checked after the negative rule so a negative amount keeps its own, more
        // specific sentence, and in decimal throughout so the one-cent tolerance is exact.
        if (Math.Abs(amountExGst + gstAmount - totalAmount) > AmountTolerance)
        {
            details = null;
            error = AmountsMustAddUpMessage;
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
