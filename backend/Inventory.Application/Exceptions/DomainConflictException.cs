namespace Inventory.Application.Exceptions;

/// <summary>
/// Signals that a request cannot proceed because of the current state of the data (for example an
/// overlapping agreement or a concurrent change), not because the input itself was invalid. The
/// message is written for the caller: the HTTP boundary's centralized exception handler returns
/// it verbatim in the <c>ProblemDetails</c> response, so it must never carry an internal path,
/// identifier meant to stay server-side, or anything derived from another actor's data.
/// </summary>
public sealed class DomainConflictException : Exception
{
    public DomainConflictException(string message)
        : base(message)
    {
    }
}
