namespace Inventory.Domain.Exceptions;

/// <summary>
/// A specialization of <see cref="DomainConflictException"/>: the request cannot proceed because
/// the current stock level conflicts with the requested adjustment, not because the input itself
/// was invalid. Its message is a fixed sentence plus the available stock count, a value the caller
/// is already entitled to see.
///
/// The HTTP handler keeps mapping this to <c>400 Bad Request</c> rather than the <c>409</c> its
/// base type otherwise maps to - see <c>InventoryApi/Http/DomainExceptionHandler.cs</c> - so this
/// inheritance change is purely about naming the true semantics of the failure, not about changing
/// the caller-facing contract.
/// </summary>
public sealed class InsufficientStockException : DomainConflictException
{
    public InsufficientStockException(int availableStock)
        : base($"Not enough products in stock. Available stock: {availableStock}")
    {
    }
}
