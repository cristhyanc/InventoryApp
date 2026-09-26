namespace Inventory.Domain.Exceptions;

/// <summary>
/// Signals that a request failed a deliberate business-rule check, not a programming error.
/// The message is written for the caller: the HTTP boundary's centralized exception handler
/// returns it verbatim in the <c>ProblemDetails</c> response, so it must never carry an internal
/// path, identifier meant to stay server-side, or anything derived from another actor's data.
/// </summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message)
        : base(message)
    {
    }
}
