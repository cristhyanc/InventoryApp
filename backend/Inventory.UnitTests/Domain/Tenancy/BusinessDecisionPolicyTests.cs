using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic rules of deciding a business application (issue #544): which status may be
/// decided, and what a rejection reason may be.
/// </summary>
public class BusinessDecisionPolicyTests
{
    /// <summary>
    /// Only a pending application is decidable. The interesting rows are the last two: an unfilled
    /// integer column reads as <c>0</c> and a newer deployment could write a value this code does
    /// not declare, and neither may be treated as something an operator can approve.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending, true)]
    [InlineData(BusinessStatus.Active, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData(BusinessStatus.Deactivated, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void Only_a_pending_business_is_decidable(BusinessStatus status, bool decidable) =>
        Assert.Equal(decidable, BusinessDecisionPolicy.IsDecidable(status));

    /// <summary>
    /// "No reason" has exactly one stored representation, <c>null</c>, so an absent field and a
    /// blank one are the same fact rather than two kinds of absence.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_blank_reason_is_stored_as_no_reason(string? reason) =>
        Assert.Null(BusinessDecisionPolicy.ValidateRejectionReason(reason));

    [Fact]
    public void A_reason_is_stored_trimmed() =>
        Assert.Equal(
            "Needs a registered ABN.",
            BusinessDecisionPolicy.ValidateRejectionReason("  Needs a registered ABN.  "));

    /// <summary>
    /// The boundary, from both sides: the limit itself is storable, and it is measured after
    /// trimming - so trailing whitespace cannot push an acceptable reason over the edge.
    /// </summary>
    [Fact]
    public void A_reason_of_exactly_the_maximum_length_is_accepted()
    {
        var longest = new string('x', BusinessDecisionPolicy.MaxRejectionReasonLength);

        Assert.Equal(longest, BusinessDecisionPolicy.ValidateRejectionReason(longest));
        Assert.Equal(longest, BusinessDecisionPolicy.ValidateRejectionReason($"  {longest}  "));
    }

    [Fact]
    public void A_reason_longer_than_the_maximum_is_refused()
    {
        var tooLong = new string('x', BusinessDecisionPolicy.MaxRejectionReasonLength + 1);

        var validation = Assert.Throws<DomainValidationException>(
            () => BusinessDecisionPolicy.ValidateRejectionReason(tooLong));

        Assert.Equal(BusinessDecisionPolicy.RejectionReasonTooLongMessage, validation.Message);
    }

    /// <summary>
    /// The two refusal sentences are written for a caller: neither names a business, a status, a
    /// member or a role, because the Super Admin's own console is not the only place a message can
    /// end up.
    /// </summary>
    [Fact]
    public void The_refusals_name_nothing_about_the_business_or_its_members()
    {
        foreach (var message in new[]
                 {
                     BusinessDecisionPolicy.NotPendingMessage,
                     BusinessDecisionPolicy.ApplicantUnavailableMessage,
                 })
        {
            Assert.NotEmpty(message);
            foreach (var status in Enum.GetNames<BusinessStatus>())
            {
                Assert.DoesNotContain(status, message, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var role in Enum.GetNames<BusinessRole>())
            {
                Assert.DoesNotContain(role, message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
