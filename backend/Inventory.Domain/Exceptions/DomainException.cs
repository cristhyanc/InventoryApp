namespace Inventory.Domain.Exceptions;

/// <summary>
/// Root of the domain exception hierarchy: a deliberate business-rule failure, never a programming
/// error, an infrastructure fault, or an unexpected condition. Every subclass's message is written
/// for the caller - the HTTP boundary's centralized exception handler
/// (<c>InventoryApi/Http/DomainExceptionHandler.cs</c>) returns it verbatim in the
/// <c>ProblemDetails</c> response - so a message here must never carry an internal path, an
/// identifier meant to stay server-side, or anything derived from another actor's data.
///
/// This type is abstract on purpose: throwing it directly would not say whether the failure is an
/// invalid request (<see cref="DomainValidationException"/>) or a request that conflicts with the
/// current state of the data (<see cref="DomainConflictException"/>), and the HTTP handler's status
/// code depends on knowing which. A use-case-specific failure that is not a domain invariant - a
/// request-validation or entity-not-found failure that only one feature's use case cares about -
/// belongs in <c>Inventory.Application</c> instead of joining this hierarchy; a provider-specific
/// failure (EF Core, Azure Blob, filesystem, HTTP, Nayax) belongs in
/// <c>Inventory.Infrastructure</c> and must be translated before it would ever reach here.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }
}
