using Inventory.Application.Access;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// Answers the one role-preview question (issue #720) with whatever a test decided, so the
/// Application components that branch on it - <c>CurrentCapabilities</c> and everything that reads
/// it - can be tested without a header, a policy or a platform administrator.
///
/// <para>It counts the calls, because the real accessor is memoised per request: several components
/// ask in one request and must not each re-evaluate the platform-admin policy.</para>
/// </summary>
public sealed class FakeRolePreviewAccessor : IRolePreviewAccessor
{
    private readonly RolePreviewAccess _access;

    public FakeRolePreviewAccessor(RolePreviewAccess access) => _access = access;

    /// <summary>A request that asked for no preview - every ordinary caller's.</summary>
    public FakeRolePreviewAccessor()
        : this(RolePreviewAccess.NotRequested)
    {
    }

    public int CallCount { get; private set; }

    /// <summary>A request being answered as <paramref name="role"/>.</summary>
    public static FakeRolePreviewAccessor Previewing(BusinessRole role) =>
        new(RolePreviewAccess.Active(role));

    /// <summary>A preview asked for by somebody who may not ask for one.</summary>
    public static FakeRolePreviewAccessor NotPermitted() => new(RolePreviewAccess.NotPermitted);

    /// <summary>A preview whose presented value named no declared role.</summary>
    public static FakeRolePreviewAccessor Invalid() => new(RolePreviewAccess.Invalid);

    public Task<RolePreviewAccess> ResolveAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_access);
    }
}
