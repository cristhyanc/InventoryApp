using Inventory.Domain.Exceptions;

namespace Inventory.Domain.MachineStock;

/// <summary>
/// What an operator may record as a machine-stock count (issue #56). A count is an observation of
/// physical reality, so the only things to check are that the quantity is a possible one and that
/// the note stays within the column it is stored in; there is no stale-state comparison, because a
/// count replaces nothing and corrects nothing - it is appended, and the ledger reports the
/// difference it absorbed.
/// </summary>
public static class MachineStockCountPolicy
{
    /// <summary>The longest note a count may carry, matching the persisted column's length.</summary>
    public const int MaxNotesLength = 500;

    /// <exception cref="DomainValidationException">The counted quantity or the note is not acceptable.</exception>
    public static void Validate(int countedQuantity, string? notes)
    {
        if (countedQuantity < 0)
            throw new DomainValidationException("A counted machine stock quantity cannot be negative.");

        if (notes is not null && notes.Length > MaxNotesLength)
            throw new DomainValidationException($"A machine stock count note cannot exceed {MaxNotesLength} characters.");
    }
}
