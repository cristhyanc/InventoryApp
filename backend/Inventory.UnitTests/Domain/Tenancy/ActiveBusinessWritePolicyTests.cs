using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The rule that a write into a business requires that business to still be trading (issue #680):
/// which status permits one, and what the refusal says.
/// </summary>
public class ActiveBusinessWritePolicyTests
{
    /// <summary>
    /// Only a trading business may be written into. The last two rows are the ones that matter: an
    /// unfilled integer column reads as <c>0</c> and a newer deployment could store a value this
    /// code does not declare, and neither is a state a write may be committed under.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active, true)]
    [InlineData(BusinessStatus.Pending, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData(BusinessStatus.Deactivated, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void Only_an_active_business_may_be_written_into(BusinessStatus status, bool allowed) =>
        Assert.Equal(allowed, ActiveBusinessWritePolicy.AllowsWrite(status));

    /// <summary>
    /// It is the same question the resolution rule asks, so the two can never answer differently
    /// for a status either of them was not updated for: a business a request may not reach is a
    /// business a request in flight may not write into.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    [InlineData((BusinessStatus)0)]
    [InlineData((BusinessStatus)99)]
    public void It_permits_a_write_exactly_where_membership_resolution_grants_access(BusinessStatus status)
    {
        var resolution = BusinessMembershipResolutionPolicy.Resolve(
        [
            new ActorBusinessMembership(
                BusinessId.From(1),
                BusinessRole.Owner,
                IsActive: true,
                status,
                StatusChangedAtUtc: new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc),
                BusinessRejectionReason: null),
        ]);

        Assert.Equal(resolution.IsResolved, ActiveBusinessWritePolicy.AllowsWrite(status));
    }

    /// <summary>
    /// The refusal is a complete sentence about the caller's own change, and it names no business,
    /// member or role - and no repair, because nobody inside a business that has been switched off
    /// can switch it back on.
    /// </summary>
    [Fact]
    public void The_refusal_states_that_the_change_was_not_saved()
    {
        Assert.StartsWith("This business", ActiveBusinessWritePolicy.NotActiveMessage, StringComparison.Ordinal);
        Assert.EndsWith(".", ActiveBusinessWritePolicy.NotActiveMessage, StringComparison.Ordinal);
        Assert.Contains("not saved", ActiveBusinessWritePolicy.NotActiveMessage, StringComparison.Ordinal);
    }
}
