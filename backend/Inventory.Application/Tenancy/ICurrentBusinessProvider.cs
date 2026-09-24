using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The Application-facing current-business (current-tenant) abstraction required by issue #64.
///
/// It is the only supported way for a use case, or for downstream infrastructure such as
/// protected document storage, to learn which business the caller owns. It exposes no claims and
/// takes no caller-supplied tenant id, so a business id can never arrive from route, query,
/// form, or JSON input.
/// </summary>
public interface ICurrentBusinessProvider
{
    /// <summary>
    /// Resolves the caller's current business, reporting the denial reason instead of throwing.
    /// Use this where the boundary wants to map the reason onto a specific HTTP response.
    /// </summary>
    Task<BusinessMembershipResolution> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the caller's current business or throws <see cref="BusinessAccessDeniedException"/>.
    /// This is the fail-closed default for ownership-scoped work: there is no return value that
    /// means "carry on unscoped".
    /// </summary>
    Task<BusinessId> RequireBusinessIdAsync(CancellationToken cancellationToken);
}
