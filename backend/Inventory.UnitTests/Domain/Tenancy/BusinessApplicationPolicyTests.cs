using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic rules of applying for a business (issue #541): what a submission must be, when
/// a second one is the same application, and which sentence a refusal carries.
///
/// <para>The validation cases here are deliberately thin. The rules themselves belong to
/// <see cref="BusinessSettingsPolicy"/> and are covered by its own tests; what matters here is that
/// an application is validated by <em>that</em> policy and not by a second one, so these assert the
/// delegation rather than re-testing every boundary of it.</para>
/// </summary>
public class BusinessApplicationPolicyTests
{
    private const string ValidAbn = "51824753556";

    [Fact]
    public void A_complete_application_is_validated_and_normalised_by_the_settings_policy()
    {
        var validated = BusinessApplicationPolicy.Validate(
            "  Coastal Vending  ",
            "  owner@coastal.example.com ",
            "51 824 753 556",
            gstRegistered: true,
            "  Australia/Brisbane ");

        Assert.Equal("Coastal Vending", validated.Name);
        Assert.Equal("owner@coastal.example.com", validated.ContactEmail);
        Assert.Equal(ValidAbn, validated.Abn);
        Assert.True(validated.GstRegistered);
        Assert.Equal("Australia/Brisbane", validated.TimeZoneId);
    }

    /// <summary>
    /// An absent ABN is the one optional part of an application, and <c>null</c> is its single
    /// representation of absence.
    /// </summary>
    [Fact]
    public void An_application_without_an_abn_is_valid_and_records_none()
    {
        var validated = BusinessApplicationPolicy.Validate(
            "Coastal Vending",
            "owner@coastal.example.com",
            abn: null,
            gstRegistered: false,
            "Australia/Sydney");

        Assert.Null(validated.Abn);
        Assert.False(validated.GstRegistered);
    }

    /// <summary>
    /// Each refusal is the settings policy's own message, which is what proves there is one rule
    /// rather than two. A blank ABN is included on purpose: it is refused rather than stored as a
    /// second kind of "not recorded".
    /// </summary>
    [Theory]
    [InlineData(null, "owner@coastal.example.com", null, true, "Australia/Sydney")]
    [InlineData("   ", "owner@coastal.example.com", null, true, "Australia/Sydney")]
    [InlineData("Coastal Vending", null, null, true, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@", null, true, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@example.com>", null, true, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", null, null, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", "", true, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", "51824753557", true, "Australia/Sydney")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", null, true, null)]
    [InlineData("Coastal Vending", "owner@coastal.example.com", null, true, "  ")]
    public void An_incomplete_or_malformed_application_is_refused(
        string? name,
        string? contactEmail,
        string? abn,
        bool? gstRegistered,
        string? timeZoneId)
    {
        Assert.Throws<DomainValidationException>(() =>
            BusinessApplicationPolicy.Validate(name, contactEmail, abn, gstRegistered, timeZoneId));
    }

    /// <summary>
    /// A name of exactly the settings policy's limit is accepted and one character more is refused,
    /// which pins that the application path reads that same bound rather than one of its own.
    /// </summary>
    [Fact]
    public void The_name_length_bound_is_the_settings_policys_own()
    {
        var atTheLimit = new string('v', BusinessSettingsPolicy.MaxNameLength);

        Assert.Equal(
            atTheLimit,
            BusinessApplicationPolicy
                .Validate(atTheLimit, "owner@coastal.example.com", null, true, "Australia/Sydney")
                .Name);

        Assert.Throws<DomainValidationException>(() => BusinessApplicationPolicy.Validate(
            atTheLimit + "v",
            "owner@coastal.example.com",
            null,
            true,
            "Australia/Sydney"));
    }

    #region Resubmission

    /// <summary>
    /// The same form again is the same application. The comparison is over the validated values, so
    /// the whitespace and the ABN spacing a person retypes are not part of it.
    /// </summary>
    [Fact]
    public void The_identical_application_submitted_again_is_a_resubmission()
    {
        var pending = Settings();
        var resubmitted = BusinessApplicationPolicy.Validate(
            "  Coastal Vending ",
            " owner@coastal.example.com",
            "51 824 753 556",
            gstRegistered: true,
            "Australia/Brisbane ");

        Assert.True(BusinessApplicationPolicy.IsResubmission(pending, resubmitted));
    }

    /// <summary>
    /// Any one setting differing makes it a different application - including a corrected
    /// capitalisation, because the stored name is the one the applicant typed and changing its case
    /// changes what the business would publish about itself.
    /// </summary>
    [Theory]
    [InlineData("Coastal vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane")]
    [InlineData("Inland Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane")]
    [InlineData("Coastal Vending", "owner@inland.example.com", ValidAbn, true, "Australia/Brisbane")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", null, true, "Australia/Brisbane")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", "53004085616", true, "Australia/Brisbane")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", ValidAbn, false, "Australia/Brisbane")]
    [InlineData("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Sydney")]
    public void A_changed_setting_makes_it_a_different_application(
        string name,
        string contactEmail,
        string? abn,
        bool gstRegistered,
        string timeZoneId)
    {
        var submitted = new BusinessSettings(name, contactEmail, abn, gstRegistered, timeZoneId);

        Assert.False(BusinessApplicationPolicy.IsResubmission(Settings(), submitted));
    }

    #endregion

    #region Refusal wording

    /// <summary>
    /// The refusal a caller who already holds an active membership gets is the standing membership
    /// sentence, and the one for a caller attached to a deactivated business is its own - exactly
    /// the two the invite redemption refuses with, from the same two constants, because the
    /// situation is the same one.
    /// </summary>
    [Theory]
    [InlineData(AccountState.Member)]
    [InlineData(AccountState.MembershipAmbiguous)]
    [InlineData(AccountState.NoMembership)]
    [InlineData(AccountState.MembershipRevoked)]
    [InlineData(AccountState.ApplicationRejected)]
    public void An_ineligible_caller_is_told_they_already_belong_somewhere(AccountState state)
    {
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            BusinessApplicationPolicy.IneligibleMessage(state));
    }

    [Fact]
    public void A_caller_attached_to_a_deactivated_business_is_told_to_be_released_first()
    {
        Assert.Equal(
            MemberInviteRedemptionPolicy.ReleaseRequiredMessage,
            BusinessApplicationPolicy.IneligibleMessage(AccountState.BusinessDeactivated));
    }

    /// <summary>
    /// The one state whose wording differs from the redemption's: a caller who already has an
    /// application waiting reaches a refusal only by submitting details that differ from it, so the
    /// sentence has to be about that rather than about already belonging somewhere.
    /// </summary>
    [Fact]
    public void A_caller_with_a_waiting_application_is_told_to_wait_for_the_decision()
    {
        var message = BusinessApplicationPolicy.IneligibleMessage(AccountState.ApplicationPending);

        Assert.Equal(BusinessApplicationPolicy.PendingApplicationDiffersMessage, message);
        Assert.NotEqual(MembershipEligibility.AlreadyAMemberMessage, message);
    }

    /// <summary>
    /// No refusal sentence names a business, a role or another member: a person told they may not
    /// apply is not thereby entitled to know where they already belong.
    /// </summary>
    [Fact]
    public void No_refusal_names_a_business_a_role_or_another_member()
    {
        foreach (var state in Enum.GetValues<AccountState>())
        {
            var message = BusinessApplicationPolicy.IneligibleMessage(state);

            Assert.DoesNotContain("Owner", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Manager", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vending", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    private static BusinessSettings Settings() =>
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, GstRegistered: true, "Australia/Brisbane");
}
