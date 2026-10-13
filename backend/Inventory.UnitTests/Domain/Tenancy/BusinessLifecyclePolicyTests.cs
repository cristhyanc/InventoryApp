using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic rules of switching a business off and on again, and of releasing its members
/// while it is off (issue #545): which status each operation may be asked of, and what a refusal
/// says.
/// </summary>
public class BusinessLifecyclePolicyTests
{
    /// <summary>
    /// Only a trading business may be deactivated. The last two rows are the ones that matter: an
    /// unfilled integer column reads as <c>0</c> and a newer deployment could store a value this
    /// code does not declare, and neither may be treated as something to offboard.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active, true)]
    [InlineData(BusinessStatus.Pending, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData(BusinessStatus.Deactivated, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void Only_an_active_business_may_be_deactivated(BusinessStatus status, bool allowed) =>
        Assert.Equal(allowed, BusinessLifecyclePolicy.CanDeactivate(status));

    /// <summary>
    /// Only a deactivated business may be reactivated: approving a pending application is #544's
    /// decision, and a refused application is not revived by this path either.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Deactivated, true)]
    [InlineData(BusinessStatus.Active, false)]
    [InlineData(BusinessStatus.Pending, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void Only_a_deactivated_business_may_be_reactivated(BusinessStatus status, bool allowed) =>
        Assert.Equal(allowed, BusinessLifecyclePolicy.CanReactivate(status));

    /// <summary>
    /// The members of a business may be listed and released by the platform administrator only
    /// while that business is deactivated. A trading business's members are its own Owner's
    /// business, and a pending or refused application is not a business anybody is stuck inside.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Deactivated, true)]
    [InlineData(BusinessStatus.Active, false)]
    [InlineData(BusinessStatus.Pending, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void Members_may_be_reached_only_while_the_business_is_deactivated(
        BusinessStatus status,
        bool allowed) =>
        Assert.Equal(allowed, BusinessLifecyclePolicy.AllowsMemberAccess(status));

    /// <summary>
    /// The refusals are written for the one caller who may reach these operations, the platform
    /// administrator, so unlike <see cref="BusinessDecisionPolicy"/>'s they may name the state the
    /// business is in - it is a column that caller's own directory already reports. What none of
    /// them may carry is a business's name, a member's name or a reason to look elsewhere, so each
    /// one is a complete sentence about the operation and nothing else.
    /// </summary>
    [Fact]
    public void Every_refusal_is_a_sentence_about_the_operation()
    {
        foreach (var message in new[]
                 {
                     BusinessLifecyclePolicy.NotActiveMessage,
                     BusinessLifecyclePolicy.NotDeactivatedMessage,
                     BusinessLifecyclePolicy.MembersUnavailableMessage,
                     BusinessLifecyclePolicy.NoActiveOwnerMessage,
                 })
        {
            Assert.NotEmpty(message);
            Assert.EndsWith(".", message, StringComparison.Ordinal);
            Assert.StartsWith("This business", message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The reactivation refusal is the sentence the acceptance criterion asks for, and it is
    /// deliberately not <see cref="BusinessOwnerRetention.LastActiveOwnerMessage"/>: that one tells
    /// the reader to give another member the Owner role first, which is advice only an Owner inside
    /// the business can act on - and a deactivated business has no reachable Owner at all.
    /// </summary>
    [Fact]
    public void The_reactivation_refusal_states_that_there_is_no_active_owner()
    {
        Assert.Contains(
            "no active Owner",
            BusinessLifecyclePolicy.NoActiveOwnerMessage,
            StringComparison.Ordinal);
        Assert.NotEqual(
            BusinessOwnerRetention.LastActiveOwnerMessage,
            BusinessLifecyclePolicy.NoActiveOwnerMessage);
    }
}
