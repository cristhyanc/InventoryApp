namespace Inventory.Domain.Exceptions;

/// <summary>
/// Signals that a request cannot proceed because of the current state of the data (for example an
/// overlapping agreement or a concurrent change), not because the input itself was invalid. The
/// message is written for the caller: the HTTP boundary's centralized exception handler returns
/// it verbatim in the <c>ProblemDetails</c> response, so it must never carry an internal path,
/// identifier meant to stay server-side, or anything derived from another actor's data.
///
/// Not sealed: <see cref="InsufficientStockException"/> specializes it for the one existing case
/// where the conflicting state is a stock level rather than an overlapping record. A new
/// specialization must keep the same caller-safe-message contract.
/// </summary>
public class DomainConflictException : DomainException
{
    public DomainConflictException(string message)
        : base(message)
    {
    }
}
