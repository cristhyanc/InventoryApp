using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Application.Onboarding;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The self-service business application of issue #541 through the real request pipeline:
/// authentication, the delegated scope, the membership-not-required marker, the business scope
/// middleware, the controller, issue #522's shared write transaction and relational SQLite with the
/// real migration history. The host has <c>SelfServiceOnboarding:Enabled</c> switched on; the
/// shipped default is covered by <see cref="BusinessApplicationDisabledApiTests"/>.
///
/// <para>One already-trading business is seeded throughout, with its own Owner and Manager, which is
/// what makes the isolation assertions meaningful: a new tenant arriving changes nothing about it,
/// an application endpoint reaches none of its data, and nothing in the request or the response
/// names a business id at all.</para>
///
/// <para>These tests are the proof that the whole authorization shape works end to end - a caller
/// who has no membership, and whom every other endpoint refuses with a bare <c>403</c>, reaches
/// exactly this one endpoint and nothing else.</para>
/// </summary>
public sealed class BusinessApplicationApiTests : IClassFixture<BusinessApplicationApiFactory>
{
    private const string Uri = "/api/onboarding/business-applications";
    private const string ValidAbn = "51824753556";

    private readonly BusinessApplicationApiFactory _factory;

    public BusinessApplicationApiTests(BusinessApplicationApiFactory factory) => _factory = factory;

    #region Applying

    /// <summary>
    /// The workflow the issue describes: somebody signing in for the first time applies, and the
    /// application waits as a <see cref="BusinessStatus.Pending"/> business they own, with their own
    /// account state reporting it.
    /// </summary>
    [Fact]
    public async Task A_person_with_no_membership_applies_and_owns_a_pending_business()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(applicant));

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = await ReadJsonAsync(response);
        Assert.Equal("Coastal Vending", applied.GetProperty("name").GetString());
        Assert.Equal("Pending", applied.GetProperty("status").GetString());

        var business = await _factory.BusinessAsync("Coastal Vending");
        Assert.Equal(business.AppliedAtUtc, applied.GetProperty("appliedAtUtc").GetDateTime());
        Assert.Equal(BusinessStatus.Pending, business.Status);
        Assert.Equal("owner@coastal.example.com", business.ContactEmail);
        Assert.Equal(ValidAbn, business.Abn);
        Assert.True(business.GstRegistered);
        Assert.Equal("Australia/Brisbane", business.TimeZoneId);
        // The state the application recorded (issue #933), stored on the pending business.
        Assert.Equal("NSW", business.State);
        Assert.NotNull(business.AppliedAtUtc);

        // Nobody has decided anything yet.
        Assert.Null(business.DecidedAtUtc);
        Assert.Null(business.RejectionReason);

        var membership = await _factory.MembershipAsync(applicant);
        Assert.Equal(business.Id, membership.BusinessId);
        Assert.Equal(BusinessRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
        Assert.Equal(membership.CreatedAtUtc, membership.StatusChangedAtUtc);

        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(applicant));
    }

    /// <summary>
    /// The response carries the application's own name, status and submission instant - and no
    /// business id, no membership key and no contact detail. An application is the business, so its
    /// id is the tenant key, and no response here hands one out.
    /// </summary>
    [Fact]
    public async Task The_response_names_no_business_id_and_no_contact_detail()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application());
        var body = await response.Content.ReadAsStringAsync();
        var business = await _factory.BusinessAsync("Coastal Vending");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["appliedAtUtc", "name", "status"],
            (await ReadJsonAsync(response)).EnumerateObject().Select(field => field.Name).Order());
        Assert.DoesNotContain("owner@coastal.example.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidAbn, body, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"id\":{business.Id}", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The membership stores the display name and email address the applicant's own validated token
    /// reported (issue #524's display-only values), so a members list shows a person rather than a
    /// GUID from the moment the business is approved.
    /// </summary>
    [Fact]
    public async Task The_new_membership_carries_the_applicants_own_display_name_and_email()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(applicant, "Dana Doe", "dana@example.test")
                .PostAsJsonAsync(Uri, Application())).StatusCode);

        var membership = await _factory.MembershipAsync(applicant);
        Assert.Equal("Dana Doe", membership.DisplayName);
        Assert.Equal("dana@example.test", membership.Email);
    }

    /// <summary>
    /// An application without an ABN is complete: the ABN is the one optional part, and
    /// <c>null</c> is its single representation of "not recorded".
    /// </summary>
    [Fact]
    public async Task An_application_without_an_abn_is_accepted_and_records_none()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant)
            .PostAsJsonAsync(Uri, Application() with { Abn = null, GstRegistered = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var business = await _factory.BusinessAsync("Coastal Vending");
        Assert.Null(business.Abn);
        Assert.False(business.GstRegistered);
    }

    /// <summary>
    /// The business that was already trading is untouched: its row, its status, its memberships and
    /// its members' own access are exactly as they were before a stranger applied.
    /// </summary>
    [Fact]
    public async Task An_application_leaves_the_existing_business_and_its_members_untouched()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application())).StatusCode);

        var existing = await _factory.BusinessAsync(BusinessApplicationApiFactory.ExistingBusinessName);
        Assert.Equal(BusinessApplicationApiFactory.ExistingBusinessId, existing.Id);
        Assert.Equal(BusinessStatus.Active, existing.Status);
        Assert.Null(existing.AppliedAtUtc);

        Assert.Equal("Member", await _factory.AccountStateOfAsync(BusinessApplicationApiFactory.OwnerObjectId));
        var access = await ReadJsonAsync(
            await _factory.Authenticated(BusinessApplicationApiFactory.OwnerObjectId).GetAsync("/api/me/access"));
        Assert.Equal("Owner", access.GetProperty("role").GetString());
        Assert.Equal(BusinessApplicationApiFactory.ExistingBusinessName, access.GetProperty("businessName").GetString());
    }

    /// <summary>
    /// The applicant reaches none of its data either, before or after applying: a Pending business
    /// resolves nothing, so every business endpoint answers the ordinary <c>403</c> and the
    /// applicant's own account state is the only thing that explains why.
    /// </summary>
    [Fact]
    public async Task A_pending_applicant_is_refused_by_every_normal_endpoint()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);
        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(applicant));

        foreach (var forbidden in new[]
        {
            "/api/me/access",
            "/api/business/current",
            "/api/business/settings",
            "/api/members",
            "/api/members/invites",
            "/api/Products",
            "/api/dashboard/summary",
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(forbidden)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/members/invites", new { role = "Owner" })).StatusCode);
    }

    #endregion

    #region Validation

    /// <summary>
    /// Every incomplete or malformed submission is a <c>400</c> carrying the Domain rule's own
    /// sentence, and writes nothing at all: no business, no membership, and the applicant's account
    /// state unchanged.
    /// </summary>
    [Theory]
    [InlineData("name-missing", BusinessSettingsPolicy.NameRequiredMessage)]
    [InlineData("name-blank", BusinessSettingsPolicy.NameRequiredMessage)]
    [InlineData("name-too-long", BusinessSettingsPolicy.NameRequiredMessage)]
    [InlineData("email-missing", BusinessContactEmail.InvalidMessage)]
    [InlineData("email-malformed", BusinessContactEmail.InvalidMessage)]
    [InlineData("gst-unstated", BusinessSettingsPolicy.GstRegistrationRequiredMessage)]
    [InlineData("abn-blank", AustralianBusinessNumber.InvalidMessage)]
    [InlineData("abn-checksum", AustralianBusinessNumber.InvalidMessage)]
    [InlineData("zone-missing", BusinessSettingsPolicy.TimeZoneRequiredMessage)]
    [InlineData("state-missing", AustralianStates.InvalidMessage)]
    [InlineData("state-blank", AustralianStates.InvalidMessage)]
    [InlineData("state-long-name", AustralianStates.InvalidMessage)]
    [InlineData("state-unknown", AustralianStates.InvalidMessage)]
    public async Task An_incomplete_or_malformed_application_is_refused_and_writes_nothing(
        string kind,
        string expectedDetail)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Invalid(kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedDetail, (await ReadJsonAsync(response)).GetProperty("detail").GetString());

        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(applicant));
    }

    /// <summary>
    /// Each of the eight states and territories is accepted and stored on the pending business,
    /// and a value typed in lower case is stored as its canonical abbreviation (issue #933). The
    /// refusals - a missing, blank, long-named or unrecognised state - are theory rows of
    /// <see cref="An_incomplete_or_malformed_application_is_refused_and_writes_nothing"/> above.
    /// </summary>
    [Theory]
    [InlineData("NSW", "NSW")]
    [InlineData("VIC", "VIC")]
    [InlineData("QLD", "QLD")]
    [InlineData("SA", "SA")]
    [InlineData("WA", "WA")]
    [InlineData("TAS", "TAS")]
    [InlineData("ACT", "ACT")]
    [InlineData("NT", "NT")]
    [InlineData(" nt ", "NT")]
    public async Task An_application_records_the_state_it_was_submitted_with(string submitted, string stored)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant)
            .PostAsJsonAsync(Uri, Application() with { State = submitted });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var business = await _factory.BusinessAsync("Coastal Vending");
        Assert.Equal(stored, business.State);
        Assert.Equal(BusinessStatus.Pending, business.Status);
    }

    /// <summary>
    /// A time zone the host's own database cannot resolve is refused centrally, on the way into the
    /// database, and still writes nothing: the business and the membership are one transaction, so a
    /// refusal at the last step takes both back.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_time_zone_is_refused_and_writes_nothing()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant)
            .PostAsJsonAsync(Uri, Application() with { TimeZoneId = "Mars/Olympus_Mons" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
    }

    /// <summary>
    /// An empty body is refused the same way. Nothing is defaulted: a missing GST registration is
    /// not <c>false</c>, and a missing name is not an empty one.
    /// </summary>
    [Fact]
    public async Task An_empty_body_is_refused()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant).PostAsync(
            Uri,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await _factory.BusinessesAsync());
    }

    /// <summary>
    /// With the feature on, a body MVC cannot bind is still <c>[ApiController]</c>'s ordinary
    /// <c>400</c> and writes nothing: the onboarding gate that runs before model binding answers only
    /// for a disabled deployment and changes nothing about an enabled one.
    /// </summary>
    [Theory]
    [InlineData("malformed-json")]
    [InlineData("wrong-field-type")]
    [InlineData("empty-body")]
    public async Task A_body_that_cannot_be_bound_is_a_bad_request_and_writes_nothing(string kind)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.Authenticated(applicant)
            .PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(applicant));
    }

    /// <summary>
    /// The same unbindable bodies from an unauthenticated caller and from one without the delegated
    /// scope are still <c>401</c> and <c>403</c>: authentication decides before the body is bound.
    /// </summary>
    [Theory]
    [InlineData("malformed-json")]
    [InlineData("wrong-field-type")]
    [InlineData("empty-body")]
    public async Task Authentication_still_decides_before_a_body_that_cannot_be_bound(string kind)
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind)))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.WithoutScope(applicant).PostAsync(Uri, BusinessApplicationApiFactory.UnbindableBody(kind)))
                .StatusCode);

        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
    }

    #endregion

    #region Eligibility

    /// <summary>
    /// A caller who already holds an active membership is refused with <c>409</c> and the standing
    /// membership sentence - one that names no business, because a person told they may not apply is
    /// not thereby entitled to know which business they already belong to. Their own access is
    /// untouched and no business is created.
    /// </summary>
    [Fact]
    public async Task An_existing_member_is_refused_with_a_conflict_and_keeps_their_own_access()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(BusinessApplicationApiFactory.ManagerObjectId)
            .PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.DoesNotContain(BusinessApplicationApiFactory.ExistingBusinessName, body, StringComparison.Ordinal);

        Assert.Single(await _factory.BusinessesAsync());
        var membership = await _factory.MembershipAsync(BusinessApplicationApiFactory.ManagerObjectId);
        Assert.Equal(BusinessApplicationApiFactory.ExistingBusinessId, membership.BusinessId);
        Assert.Equal(BusinessRole.Manager, membership.Role);
    }

    /// <summary>
    /// A caller whose own business was deactivated gets the refusal with its own sentence: they must
    /// first be released by the platform administrator, because a deactivated business has no
    /// reachable Owner who could revoke their membership. It still names no business.
    /// </summary>
    [Fact]
    public async Task A_caller_whose_business_was_deactivated_is_told_to_be_released_first()
    {
        await _factory.ResetAsync();
        var member = BusinessApplicationApiFactory.NewApplicantObjectId();
        var closedId = await _factory.SeedBusinessAsync("Closed Vending", BusinessStatus.Deactivated);
        await _factory.SeedMembershipAsync(closedId, member, BusinessRole.Owner, isActive: true);

        Assert.Equal("BusinessDeactivated", await _factory.AccountStateOfAsync(member));

        var response = await _factory.Authenticated(member).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            MemberInviteRedemptionPolicy.ReleaseRequiredMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.Contains("platform administrator", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Closed Vending", body, StringComparison.Ordinal);

        Assert.Equal(2, (await _factory.BusinessesAsync()).Count);
    }

    /// <summary>
    /// Releasing that person - the Super Admin revoking the membership a deactivated business left
    /// them attached to - makes them eligible, and they then apply successfully. The revoked record
    /// stays.
    /// </summary>
    [Fact]
    public async Task A_released_member_may_apply()
    {
        await _factory.ResetAsync();
        var released = BusinessApplicationApiFactory.NewApplicantObjectId();
        var closedId = await _factory.SeedBusinessAsync("Closed Vending", BusinessStatus.Deactivated);
        await _factory.SeedMembershipAsync(closedId, released, BusinessRole.Owner, isActive: false);

        Assert.Equal("MembershipRevoked", await _factory.AccountStateOfAsync(released));

        var response = await _factory.Authenticated(released).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(released));

        // Two records for them: the revoked one that is kept, and the new Owner membership.
        var memberships = await _factory.MembershipsOfAsync(released);
        Assert.Equal(2, memberships.Count);
        Assert.Single(memberships.Where(membership => !membership.IsActive && membership.BusinessId == closedId));
        Assert.Single(memberships.Where(membership => membership.IsActive));
    }

    /// <summary>
    /// A caller whose membership belongs to a business still awaiting its own decision is refused:
    /// they hold an active membership, and the one-active-membership rule reads no business state at
    /// all. The sentence is the pending-application one, because submitting different details to a
    /// waiting application is the only way to reach it.
    /// </summary>
    [Fact]
    public async Task A_caller_with_a_pending_application_elsewhere_is_refused()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var pendingId = await _factory.SeedBusinessAsync("Someone Elses Application", BusinessStatus.Pending);
        await _factory.SeedMembershipAsync(pendingId, applicant, BusinessRole.Owner, isActive: true);

        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(applicant));

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            BusinessApplicationPolicy.PendingApplicationDiffersMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.Equal(2, (await _factory.BusinessesAsync()).Count);
    }

    #endregion

    #region Retrying and reapplying

    /// <summary>
    /// Submitting the identical application again while it is pending answers with the application
    /// that is already waiting - the same name and the same submission instant, which is what makes
    /// it visibly the same one - and creates no second business.
    /// </summary>
    [Fact]
    public async Task Resubmitting_the_identical_application_returns_the_one_already_waiting()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        var first = await ReadJsonAsync(await client.PostAsJsonAsync(Uri, Application()));
        var business = await _factory.BusinessAsync("Coastal Vending");

        var second = await client.PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var again = await ReadJsonAsync(second);
        Assert.Equal(first.GetProperty("name").GetString(), again.GetProperty("name").GetString());
        Assert.Equal("Pending", again.GetProperty("status").GetString());
        Assert.Equal(
            first.GetProperty("appliedAtUtc").GetDateTime(),
            again.GetProperty("appliedAtUtc").GetDateTime());

        // One business, one membership, and the stored row untouched.
        Assert.Equal(2, (await _factory.BusinessesAsync()).Count);
        Assert.Single(await _factory.MembershipsOfAsync(applicant));
        var stillThere = await _factory.BusinessAsync("Coastal Vending");
        Assert.Equal(business.Id, stillThere.Id);
        Assert.Equal(business.AppliedAtUtc, stillThere.AppliedAtUtc);
    }

    /// <summary>
    /// Whitespace and ABN spacing a person retypes are not part of the application: the comparison
    /// is over the validated values, so the same form submitted with different spacing is still the
    /// same application.
    /// </summary>
    [Fact]
    public async Task A_resubmission_differing_only_in_spacing_is_the_same_application()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);

        var second = await client.PostAsJsonAsync(
            Uri,
            Application() with { Name = "  Coastal Vending ", Abn = "51 824 753 556" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await _factory.BusinessesAsync()).Count);
    }

    /// <summary>
    /// A submission that differs is refused with <c>409</c> and changes nothing: the details an
    /// operator is reviewing are not silently overwritten, and no second application is created.
    /// </summary>
    [Fact]
    public async Task A_resubmission_with_different_details_is_refused_and_changes_nothing()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);
        var stored = await _factory.BusinessAsync("Coastal Vending");

        var second = await client.PostAsJsonAsync(
            Uri,
            Application() with { Name = "Inland Vending", ContactEmail = "owner@inland.example.com" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            BusinessApplicationPolicy.PendingApplicationDiffersMessage,
            (await ReadJsonAsync(second)).GetProperty("detail").GetString());

        var unchanged = await _factory.BusinessAsync("Coastal Vending");
        Assert.Equal(stored.Id, unchanged.Id);
        Assert.Equal("owner@coastal.example.com", unchanged.ContactEmail);
        Assert.Equal(2, (await _factory.BusinessesAsync()).Count);
    }

    /// <summary>
    /// After a rejection a new application creates a new business, and the rejected one is kept: the
    /// refused application and its revoked membership stay as the record of what happened, and the
    /// applicant's account state moves from <c>ApplicationRejected</c> to <c>ApplicationPending</c>.
    /// </summary>
    [Fact]
    public async Task A_rejected_applicant_may_apply_again_and_the_rejected_application_is_kept()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var rejectedId = await _factory.SeedBusinessAsync(
            "First Attempt",
            BusinessStatus.Rejected,
            appliedAtUtc: BusinessApplicationApiFactory.CreatedAt,
            decidedAtUtc: BusinessApplicationApiFactory.CreatedAt.AddDays(1),
            rejectionReason: "The ABN provided belongs to another entity.");
        await _factory.SeedMembershipAsync(rejectedId, applicant, BusinessRole.Owner, isActive: false);

        Assert.Equal("ApplicationRejected", await _factory.AccountStateOfAsync(applicant));

        var response = await _factory.Authenticated(applicant).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(applicant));

        // The rejected application is exactly as it was - a new application is never a correction
        // of an old one.
        var rejected = await _factory.BusinessAsync("First Attempt");
        Assert.Equal(BusinessStatus.Rejected, rejected.Status);
        Assert.Equal("The ABN provided belongs to another entity.", rejected.RejectionReason);
        Assert.Equal(BusinessApplicationApiFactory.CreatedAt.AddDays(1), rejected.DecidedAtUtc);

        var created = await _factory.BusinessAsync("Coastal Vending");
        Assert.NotEqual(rejectedId, created.Id);
        Assert.Equal(BusinessStatus.Pending, created.Status);

        var memberships = await _factory.MembershipsOfAsync(applicant);
        Assert.Equal(2, memberships.Count);
        Assert.Single(memberships.Where(membership => !membership.IsActive && membership.BusinessId == rejectedId));
        Assert.Single(memberships.Where(membership => membership.IsActive && membership.BusinessId == created.Id));
    }

    #endregion

    #region Authorization

    /// <summary>
    /// The marker removes the membership requirement and nothing else. An unauthenticated request is
    /// still <c>401</c>: "membership not required" is not "anonymous".
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_refused()
    {
        await _factory.ResetAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(await _factory.BusinessesAsync());
    }

    /// <summary>
    /// The delegated <c>access_as_user</c> scope is required too, and its absence is the same
    /// <c>403</c> every other endpoint answers a missing scope with - the marker does not touch it.
    /// Nothing is written for such a request.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_delegated_scope_is_refused()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        var response = await _factory.WithoutScope(applicant).PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await _factory.BusinessesAsync());
        Assert.Empty(await _factory.MembershipsOfAsync(applicant));
    }

    /// <summary>
    /// The exemption is this endpoint's alone. The same caller, with the same headers, reaches the
    /// application endpoint and is refused by everything else with the unchanged <c>403</c> -
    /// including the Owner-facing business settings, which is the pairing that matters: being able to
    /// apply for a business must not be a way to read or change one.
    /// </summary>
    [Fact]
    public async Task The_same_business_less_caller_reaches_nothing_else()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        foreach (var forbidden in new[]
        {
            "/api/me/access",
            "/api/business/current",
            "/api/business/settings",
            "/api/members",
            "/api/Products",
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(forbidden)).StatusCode);
        }

        // And the one endpoint they may call still works.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);
    }

    /// <summary>
    /// The application runs with a denied business scope, so it reads no business data: the request
    /// that creates a tenant cannot also see the existing business's products, sites or members. It
    /// is asserted from the caller's side immediately after a successful application, with the same
    /// client - a scope that had been resolved would show up as those endpoints answering.
    /// </summary>
    [Fact]
    public async Task Applying_grants_access_to_nothing_at_all()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);

        // A Pending business resolves nothing, so even the applicant's own new business is
        // unreachable until it is approved.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/Products")).StatusCode);
    }

    #endregion

    #region The flag as account state reports it

    /// <summary>
    /// On this host the flag is on, so <c>GET /api/me/account-state</c> reports it - the real value
    /// rather than the placeholder <c>false</c> issue #523 shipped, and the same value the endpoint
    /// itself reads.
    /// </summary>
    [Fact]
    public async Task Account_state_reports_onboarding_as_enabled()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            "True",
            await _factory.OnboardingEnabledOfAsync(BusinessApplicationApiFactory.NewApplicantObjectId()));

        // It describes the deployment, not the caller: a member sees it too, and the endpoint still
        // refuses them.
        Assert.Equal(
            "True",
            await _factory.OnboardingEnabledOfAsync(BusinessApplicationApiFactory.OwnerObjectId));
    }

    #endregion

    #region Rate limit

    /// <summary>
    /// The per-applicant submission limit: once it is spent the endpoint answers <c>429</c> with
    /// <c>Retry-After</c> and writes nothing, and the refusal names no count, no window and no
    /// business. Validation failures are deliberately not counted, so the attempts here are
    /// well-formed conflicts.
    /// </summary>
    [Fact]
    public async Task Too_many_applications_are_refused_with_a_retry_after()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        // An active membership elsewhere, so each attempt is a well-formed submission that is
        // refused for a reason other than the limit - which is what lets the allowance run out
        // without a business ever being created.
        await _factory.SeedMembershipAsync(
            BusinessApplicationApiFactory.ExistingBusinessId,
            applicant,
            BusinessRole.Operator,
            isActive: true);

        var client = _factory.Authenticated(applicant);

        for (var attempt = 0; attempt < BusinessApplicationRateLimiter.MaxApplicationsPerWindow; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await client.PostAsJsonAsync(Uri, Application())).StatusCode);
        }

        var refused = await client.PostAsJsonAsync(Uri, Application());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.True(refused.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        // The fixed sentence: no count, no window and no business in it.
        Assert.Equal(
            "You have submitted too many business applications recently. Wait a few minutes and try again.",
            (await ReadJsonAsync(refused)).GetProperty("detail").GetString());
        Assert.DoesNotContain(
            BusinessApplicationApiFactory.ExistingBusinessName,
            await refused.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Single(await _factory.BusinessesAsync());
    }

    #endregion

    #region Auditing

    /// <summary>
    /// One audit event per application, carrying the actor and the new business id - and neither the
    /// business name, the contact address, the ABN, nor the applicant's own name or email address.
    /// The assertions are over everything the host logged for the request, not one logger, because
    /// personal data reaching any category is the failure that matters.
    /// </summary>
    [Fact]
    public async Task An_application_is_audited_without_any_name_or_email_address()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();

        _factory.ClearLog();
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(applicant, "Dana Doe", "dana@example.test")
                .PostAsJsonAsync(Uri, Application())).StatusCode);

        var business = await _factory.BusinessAsync("Coastal Vending");
        var logged = _factory.LoggedText();

        Assert.Contains("Business application audit.", logged, StringComparison.Ordinal);
        Assert.Contains(applicant, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"business {business.Id}", logged, StringComparison.Ordinal);

        Assert.DoesNotContain("Coastal Vending", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("owner@coastal.example.com", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidAbn, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Dana Doe", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("dana@example.test", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused submission and a resubmission are both audited as nothing: neither created a
    /// business, and an audit trail that recorded them would answer "how many tenants were created?"
    /// wrongly. The refusals are still visible, as the boundary's own warnings.
    /// </summary>
    [Fact]
    public async Task A_refused_submission_and_a_resubmission_are_audited_as_nothing()
    {
        await _factory.ResetAsync();
        var applicant = BusinessApplicationApiFactory.NewApplicantObjectId();
        var client = _factory.Authenticated(applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);

        _factory.ClearLog();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, Application())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync(Uri, Application() with { Name = "Inland Vending" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _factory.Authenticated(BusinessApplicationApiFactory.NewApplicantObjectId())
                .PostAsJsonAsync(Uri, Invalid("email-malformed"))).StatusCode);

        Assert.DoesNotContain("Business application audit.", _factory.LoggedText(), StringComparison.Ordinal);
    }

    #endregion

    private static BusinessApplicationBody Application() =>
        new("Coastal Vending", "owner@coastal.example.com", ValidAbn, true, "Australia/Brisbane", "NSW");

    /// <summary>
    /// One of the ways an application can be incomplete or malformed. They are collected here so the
    /// theory above can assert that each one produces the Domain rule's own sentence and writes
    /// nothing.
    /// </summary>
    private static BusinessApplicationBody Invalid(string kind) => kind switch
    {
        "name-missing" => Application() with { Name = null },
        "name-blank" => Application() with { Name = "   " },
        "name-too-long" => Application() with
        {
            Name = new string('v', BusinessSettingsPolicy.MaxNameLength + 1),
        },
        "email-missing" => Application() with { ContactEmail = null },
        "email-malformed" => Application() with { ContactEmail = "owner@example.com>" },
        "gst-unstated" => Application() with { GstRegistered = null },
        "abn-blank" => Application() with { Abn = "" },
        "abn-checksum" => Application() with { Abn = "51824753557" },
        "zone-missing" => Application() with { TimeZoneId = null },
        "state-missing" => Application() with { State = null },
        "state-blank" => Application() with { State = "   " },
        "state-long-name" => Application() with { State = "New South Wales" },
        "state-unknown" => Application() with { State = "NZ" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown invalid-application case."),
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// The request body as a client sends it, with every field nullable so a test can omit one
    /// exactly as a form would. It is deliberately not the API's own DTO: these tests speak JSON to
    /// the endpoint, and a body built from the contract type could not express "this field was not
    /// sent at all".
    /// </summary>
    private sealed record BusinessApplicationBody(
        string? Name,
        string? ContactEmail,
        string? Abn,
        bool? GstRegistered,
        string? TimeZoneId,
        string? State);
}
