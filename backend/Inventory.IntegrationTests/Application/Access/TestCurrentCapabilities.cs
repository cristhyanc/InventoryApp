using Inventory.Application.Access;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.SupportSessions;

namespace InventoryApi.Tests.Application.Access;

/// <summary>
/// Builds the real <see cref="CurrentCapabilities"/> over the shared fakes, for the use-case tests
/// that have to construct it but are not themselves about who may see what.
///
/// <para>It is the real type rather than a fake on purpose: <see cref="CurrentCapabilities"/> is the
/// one place that chooses between a member's role and a support session's fixed set, so a test
/// double of it could let a use case's redaction pass against a table the application does not
/// use.</para>
/// </summary>
public static class TestCurrentCapabilities
{
    /// <summary>
    /// The capabilities of a member of <paramref name="businessId"/> holding
    /// <paramref name="role"/>, outside any support session.
    /// </summary>
    public static CurrentCapabilities ForRole(BusinessRole role, int businessId = 1) =>
        new(new FakeSupportSessionAccessor(), new FakeCurrentBusinessProvider(businessId, role));

    /// <summary>
    /// An Owner's capabilities: every declared capability, which is what a test that predates
    /// capability-dependent behaviour implicitly assumed.
    /// </summary>
    public static CurrentCapabilities Owner => ForRole(BusinessRole.Owner);
}
