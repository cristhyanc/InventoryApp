using Inventory.Application.Machines;

namespace Inventory.Application.Expenses;

/// <summary>
/// The one check that an operating expense names a machine this business actually operates (issue
/// #780), shared by <see cref="CreateOperatingExpense"/> and <see cref="UpdateOperatingExpense"/> so
/// the two cannot answer differently.
///
/// <para>Since issue #780 the expense's machine is a local machine row (<c>Machines.Id</c>), not the
/// provider's machine number, so "is this a machine of mine" is a question with an answer. The
/// machine store reads through the ordinary <c>AppDbContext</c> tenant query filters, so another
/// business's machine is invisible rather than refused by a predicate written here, and no business
/// id is involved at all.</para>
///
/// <para>A deactivated machine is accepted: it is a machine the business still operated when the
/// cost was incurred, and refusing it would make a historical expense uneditable the day its machine
/// is retired.</para>
/// </summary>
internal static class OperatingExpenseMachine
{
    /// <summary>
    /// Whether <paramref name="machineId"/> may be stored on an expense of the caller's business.
    /// <c>null</c> - a whole-business expense - is always allowed.
    /// </summary>
    public static async Task<bool> IsThisBusinessesAsync(
        ILocalMachineStore machines, long? machineId, CancellationToken cancellationToken) =>
        machineId is not { } id || await machines.FindByIdAsync(id, cancellationToken) is not null;
}
