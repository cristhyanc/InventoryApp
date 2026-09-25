namespace Inventory.Application.Documents;

/// <summary>
/// The kinds of uploaded business document the application stores. The Application layer names
/// them by what they are; how a category maps onto a folder, container or blob prefix is an
/// adapter concern and must not leak through this port.
/// </summary>
public enum DocumentCategory
{
    /// <summary>The scan or photo supporting a <c>Purchase</c> record.</summary>
    PurchaseDocument = 1,

    /// <summary>The supporting document attached to an <c>OperatingExpense</c>.</summary>
    ExpenseAttachment = 2,
}
