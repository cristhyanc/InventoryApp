using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// The editable fields of an operating expense, shared by create and update. Attachment metadata
/// is deliberately separate (see <see cref="OperatingExpenseAttachmentMetadata"/>) because it is
/// optional and independently replaceable.
///
/// <para><c>MachineId</c> is the business's own machine row (<c>Machines.Id</c>, issue #780), not
/// the provider's machine number, and is checked against the business's machines before anything is
/// stored. <c>SiteId</c> is still the provider's site identifier: a site is known only from the
/// live fleet read, and nothing local models one yet.</para>
/// </summary>
public sealed record OperatingExpenseFields(
    DateTime ExpenseDate,
    ExpenseCategory Category,
    string Description,
    decimal AmountExGst,
    decimal GstAmount,
    decimal TotalAmount,
    int? SupplierId,
    long? SiteId,
    long? MachineId,
    DateTime? ServicePeriodStart,
    DateTime? ServicePeriodEnd,
    string? Notes);
