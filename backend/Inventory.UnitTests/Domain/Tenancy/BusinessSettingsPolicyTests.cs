using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The business-settings rules of issue #548: what a submitted set of settings must be, and what a
/// save actually changed - including the state or territory issue #933 added.
///
/// The ABNs used here are the Australian Business Register's documented example and a deliberately
/// broken variant of it, so the test proves the real check digit rather than one this code
/// invented.
/// </summary>
public class BusinessSettingsPolicyTests
{
    private const string ValidAbn = "51824753556";
    private const string Sydney = "Australia/Sydney";
    private const string Nsw = "NSW";

    [Fact]
    public void A_complete_submission_is_normalised_and_accepted()
    {
        var settings = BusinessSettingsPolicy.Validate(
            "  Vending Co  ",
            " owner@example.com ",
            "51 824 753 556",
            gstRegistered: true,
            timeZoneId: " Australia/Sydney ",
            state: " nsw ");

        Assert.Equal("Vending Co", settings.Name);
        Assert.Equal("owner@example.com", settings.ContactEmail);
        Assert.Equal(ValidAbn, settings.Abn);
        Assert.True(settings.GstRegistered);
        Assert.Equal(Sydney, settings.TimeZoneId);
        Assert.Equal(Nsw, settings.State);
    }

    /// <summary>
    /// The eight states and territories, each accepted and each stored as its canonical
    /// abbreviation (issue #933). The list is written out here rather than read from the vocabulary
    /// under test, so a value quietly added to or removed from it fails this test.
    /// </summary>
    [Theory]
    [InlineData("NSW")]
    [InlineData("VIC")]
    [InlineData("QLD")]
    [InlineData("SA")]
    [InlineData("WA")]
    [InlineData("TAS")]
    [InlineData("ACT")]
    [InlineData("NT")]
    public void Every_Australian_state_and_territory_is_accepted(string state)
    {
        var settings = BusinessSettingsPolicy.Validate(
            "Vending Co",
            "owner@example.com",
            null,
            true,
            Sydney,
            state);

        Assert.Equal(state, settings.State);
    }

    /// <summary>
    /// Anything outside the eight is refused with the vocabulary's own sentence - a missing value,
    /// a blank one, a long name, another country's subdivision, and a plausible-looking
    /// abbreviation that is not one of ours. A state is never guessed at: a business recorded as
    /// trading somewhere it does not would be offered the wrong suppliers (#817).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("New South Wales")]
    [InlineData("NSWW")]
    [InlineData("AUS")]
    [InlineData("NZ")]
    [InlineData("CA")]
    public void A_submitted_state_that_is_not_one_of_the_eight_is_refused(string? state)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, Sydney, state));

        Assert.Equal(AustralianStates.InvalidMessage, exception.Message);
    }

    /// <summary>
    /// The state is not derived from the time zone, which is the whole reason the setting exists:
    /// <c>Australia/Sydney</c> is New South Wales and the Australian Capital Territory alike, so
    /// the submitted value is what is stored and a zone never supplies a missing one.
    /// </summary>
    [Fact]
    public void A_state_is_never_inferred_from_the_time_zone()
    {
        Assert.Equal(
            "ACT",
            BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, Sydney, "ACT").State);
        Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, Sydney, null));
    }

    /// <summary>
    /// The ABN is the one optional setting, and an absent one has exactly one representation. A
    /// blank string is refused rather than stored as a second kind of "not recorded".
    /// </summary>
    [Fact]
    public void An_absent_ABN_is_accepted_as_not_recorded()
    {
        var settings = BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, Sydney, Nsw);

        Assert.Null(settings.Abn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("51824753557")]
    [InlineData("5182475355")]
    [InlineData("51-824-753-556")]
    public void A_submitted_ABN_that_is_not_one_is_refused(string abn)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", abn, true, Sydney, Nsw));

        Assert.Equal(AustralianBusinessNumber.InvalidMessage, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void A_missing_name_is_refused(string? name)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate(name, "owner@example.com", null, true, Sydney, Nsw));

        Assert.Equal(BusinessSettingsPolicy.NameRequiredMessage, exception.Message);
    }

    [Fact]
    public void A_name_longer_than_the_bound_is_refused_and_the_bound_itself_is_accepted()
    {
        var longest = new string('a', BusinessSettingsPolicy.MaxNameLength);

        Assert.Equal(
            longest,
            BusinessSettingsPolicy.Validate(longest, "owner@example.com", null, true, Sydney, Nsw).Name);
        Assert.Throws<DomainValidationException>(() => BusinessSettingsPolicy.Validate(
            longest + "a",
            "owner@example.com",
            null,
            true,
            Sydney,
            Nsw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owner@")]
    [InlineData("owner")]
    public void A_missing_or_malformed_contact_address_is_refused(string? contactEmail)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", contactEmail, null, true, Sydney, Nsw));

        Assert.Equal(BusinessContactEmail.InvalidMessage, exception.Message);
    }

    /// <summary>
    /// "Registered", "not registered" and "not stated" are three different facts, so an unstated
    /// registration is refused rather than bound to <c>false</c>: recording a business as not
    /// registered for GST because a field was missing would be inventing a tax fact.
    /// </summary>
    [Fact]
    public void An_unstated_GST_registration_is_refused_rather_than_read_as_not_registered()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, null, Sydney, Nsw));

        Assert.Equal(BusinessSettingsPolicy.GstRegistrationRequiredMessage, exception.Message);
    }

    [Fact]
    public void A_stated_registration_of_false_is_accepted()
    {
        var settings = BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, false, Sydney, Nsw);

        Assert.False(settings.GstRegistered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_time_zone_is_refused(string? timeZoneId)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, timeZoneId, Nsw));

        Assert.Equal(BusinessSettingsPolicy.TimeZoneRequiredMessage, exception.Message);
    }

    /// <summary>
    /// Whether a present id is a zone the host can keep a calendar in is not this layer's question:
    /// it is answered centrally on the way into the database, for every write path to a business
    /// alike. The policy must not pretend to answer it, because two validators with two answers is
    /// exactly what that central rule exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("AUS Eastern Standard Time")]
    public void A_present_time_zone_id_is_not_resolved_here(string timeZoneId)
    {
        var settings = BusinessSettingsPolicy.Validate("Vending Co", "owner@example.com", null, true, timeZoneId, Nsw);

        Assert.Equal(timeZoneId, settings.TimeZoneId);
    }

    /// <summary>
    /// A refusal names the rule and never the value: an address or an ABN echoed into an exception
    /// message ends up in a problem detail and a log, which is the one place this contract says
    /// they must never be.
    /// </summary>
    [Fact]
    public void A_refusal_never_echoes_the_submitted_address_or_ABN()
    {
        var badEmail = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate("Vending Co", "owner@bad", null, true, Sydney, Nsw));
        var badAbn = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate(
                "Vending Co",
                "owner@example.com",
                "51824753557",
                true,
                Sydney,
                Nsw));
        var badState = Assert.Throws<DomainValidationException>(
            () => BusinessSettingsPolicy.Validate(
                "Vending Co",
                "owner@example.com",
                null,
                true,
                Sydney,
                "Queensland!"));

        Assert.DoesNotContain("owner@bad", badEmail.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("51824753557", badAbn.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Queensland!", badState.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_resubmission_changes_nothing()
    {
        var current = new BusinessSettings("Vending Co", "owner@example.com", ValidAbn, true, Sydney, Nsw);

        Assert.Empty(BusinessSettingsPolicy.ChangedFields(current, current));
    }

    [Fact]
    public void Every_changed_setting_is_reported_in_declaration_order()
    {
        var current = new BusinessSettings("Vending Co", "owner@example.com", ValidAbn, true, Sydney, Nsw);
        var submitted = new BusinessSettings(
            "Renamed Co",
            "new@example.com",
            null,
            false,
            "America/New_York",
            "VIC");

        Assert.Equal(
            [
                BusinessSettingsField.Name,
                BusinessSettingsField.ContactEmail,
                BusinessSettingsField.Abn,
                BusinessSettingsField.GstRegistered,
                BusinessSettingsField.TimeZoneId,
                BusinessSettingsField.State,
            ],
            BusinessSettingsPolicy.ChangedFields(current, submitted));
    }

    /// <summary>
    /// Recording a value that was never recorded is a change, which is how a business that predates
    /// the application form fills its profile in; so is clearing the ABN back to "not recorded".
    /// </summary>
    [Fact]
    public void Recording_and_clearing_an_unrecorded_value_are_both_changes()
    {
        var unrecorded = new BusinessSettings("Vending Co", null, null, null, Sydney, null);
        var recorded = new BusinessSettings("Vending Co", "owner@example.com", ValidAbn, false, Sydney, Nsw);

        Assert.Equal(
            [
                BusinessSettingsField.ContactEmail,
                BusinessSettingsField.Abn,
                BusinessSettingsField.GstRegistered,
                BusinessSettingsField.State,
            ],
            BusinessSettingsPolicy.ChangedFields(unrecorded, recorded));
        Assert.Equal(
            [BusinessSettingsField.Abn],
            BusinessSettingsPolicy.ChangedFields(
                recorded,
                recorded with { Abn = null }));
    }

    /// <summary>
    /// Case is part of what the business publishes about itself, so changing it is a change. An
    /// audit trail that called a renamed business unchanged would be wrong.
    /// </summary>
    [Fact]
    public void A_change_of_case_is_a_change()
    {
        var current = new BusinessSettings("Vending Co", "owner@example.com", null, true, Sydney, Nsw);

        Assert.Equal(
            [BusinessSettingsField.Name, BusinessSettingsField.ContactEmail],
            BusinessSettingsPolicy.ChangedFields(
                current,
                current with { Name = "VENDING CO", ContactEmail = "Owner@example.com" }));
    }
}
