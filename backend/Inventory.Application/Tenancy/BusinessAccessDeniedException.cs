using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// Thrown by <see cref="ICurrentBusinessProvider.RequireBusinessIdAsync"/> when the caller has no
/// current business. It exists so the fail-closed path cannot be ignored by accident: a use case
/// that forgets to check a result would otherwise continue unscoped.
///
/// The message carries only the denial reason - never the actor's identity, token, or claim
/// values - so it is safe to log.
/// </summary>
public sealed class BusinessAccessDeniedException : Exception
{
    public BusinessAccessDeniedException(BusinessAccessDenialReason reason)
        : base($"Access denied: no current business could be resolved for the caller ({reason}).")
    {
        Reason = reason;
    }

    public BusinessAccessDeniedException()
        : this(BusinessAccessDenialReason.NotAuthenticated)
    {
    }

    public BusinessAccessDeniedException(string message)
        : base(message)
    {
        Reason = BusinessAccessDenialReason.NotAuthenticated;
    }

    public BusinessAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = BusinessAccessDenialReason.NotAuthenticated;
    }

    public BusinessAccessDenialReason Reason { get; }
}
