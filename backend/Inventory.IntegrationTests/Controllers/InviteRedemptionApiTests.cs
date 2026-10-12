using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

// The entity and the Application's reported invite share a name, in different layers. The row is
// what these tests inspect outside the boundary, so it gets the alias.
using MemberInviteRow = Inventory.Infrastructure.Models.MemberInvite;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The invite redemption of issue #532 through the real request pipeline: authentication, the
/// required scope, the membership-not-required marker, the business scope middleware, the
/// controller, issue #522's write transaction and relational SQLite with the real migration
/// history.
///
/// <para>Two businesses are seeded throughout, each with its own Owner and its own invite, which is
/// what makes the isolation assertions meaningful: a redemption reaches exactly the business whose
/// link it presented, and nothing in the request or the response names a business at all.</para>
///
/// <para>These tests are the proof that the whole authorization shape works end to end - a caller
/// who has no membership, and whom every other endpoint refuses with a bare <c>403</c>, reaches
/// exactly this one and nothing else.</para>
/// </summary>
public sealed class InviteRedemptionApiTests : IClassFixture<InviteRedemptionApiTests.ApiFactory>
{
    private const string Uri = "/api/invites/redeem";

    private readonly ApiFactory _factory;

    public InviteRedemptionApiTests(ApiFactory factory) => _factory = factory;

    #region Joining

    /// <summary>
    /// The whole workflow the issue describes: an Owner creates an invite, a person who belongs to
    /// nowhere opens the link, signs in, redeems it, and is a member of that business with the
    /// invite's role - visible on the Owner's member list and on their own access response.
    /// </summary>
    [Fact]
    public async Task A_person_with_no_membership_joins_the_business_with_the_invites_role()
    {
        await _factory.ResetAsync();

        var created = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId)
                .PostAsJsonAsync("/api/members/invites", new { role = "Manager", note = "New site manager" }));
        var token = created.GetProperty("token").GetString()!;
        var inviteId = created.GetProperty("invite").GetProperty("inviteId").GetInt32();

        var joiner = _factory.Authenticated(ApiFactory.JoinerObjectId);

        // Before: every business endpoint refuses them, and their account state says why.
        Assert.Equal(HttpStatusCode.Forbidden, (await joiner.GetAsync("/api/me/access")).StatusCode);
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(ApiFactory.JoinerObjectId));

        var response = await joiner.PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var redeemed = await ReadJsonAsync(response);
        Assert.Equal("Manager", redeemed.GetProperty("role").GetString());

        // After: they are a member of the inviting business, with the invite's role.
        var access = await ReadJsonAsync(await joiner.GetAsync("/api/me/access"));
        Assert.Equal("Manager", access.GetProperty("role").GetString());
        Assert.Equal("Vending Co", access.GetProperty("businessName").GetString());
        Assert.Equal("Member", await _factory.AccountStateOfAsync(ApiFactory.JoinerObjectId));

        // And the invite is closed against the membership it created, which is what makes "how did
        // this person get in?" answerable from the Owner's own list.
        var invite = await _factory.InviteAsync(inviteId);
        Assert.Equal(MemberInviteStatus.Used, invite.Status);
        Assert.NotNull(invite.UsedAtUtc);
        Assert.NotNull(invite.RedeemedByMembershipId);

        var membership = await _factory.MembershipAsync(ApiFactory.JoinerObjectId);
        Assert.Equal(ApiFactory.BusinessId, membership.BusinessId);
        Assert.Equal(BusinessRole.Manager, membership.Role);
        Assert.True(membership.IsActive);
        Assert.Equal(invite.RedeemedByMembershipId, membership.Id);
        Assert.Equal(membership.CreatedAtUtc, membership.StatusChangedAtUtc);
    }

    /// <summary>
    /// The response names no business and carries no membership key or token: the one endpoint a
    /// person with no membership may call must not publish a business, in a refusal or in a success.
    /// </summary>
    [Fact]
    public async Task The_success_response_carries_only_the_role()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Operator, "a-known-token");

        var response = await _factory.Authenticated(ApiFactory.JoinerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("""{"role":"Operator"}""", body);
        Assert.DoesNotContain("Vending Co", body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The membership stores the display name and email address the joiner's own validated token
    /// reported (issue #524's display-only values), so the Owner's member list shows a person rather
    /// than a GUID the moment they join.
    /// </summary>
    [Fact]
    public async Task The_new_membership_carries_the_joiners_own_display_name_and_email()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "with-a-profile");

        var client = _factory.Authenticated(ApiFactory.JoinerObjectId, "Dana Doe", "dana@example.test");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Uri, new { token })).StatusCode);

        var membership = await _factory.MembershipAsync(ApiFactory.JoinerObjectId);
        Assert.Equal("Dana Doe", membership.DisplayName);
        Assert.Equal("dana@example.test", membership.Email);
    }

    /// <summary>
    /// A person this business revoked rejoins by reactivating their own row, with the invite's role:
    /// one row per person per business, and the record that they were once revoked stays.
    /// </summary>
    [Fact]
    public async Task A_revoked_member_rejoins_by_reactivating_their_own_row()
    {
        await _factory.ResetAsync();
        var membershipId = await _factory.SeedMembershipAsync(
            ApiFactory.BusinessId,
            ApiFactory.JoinerObjectId,
            BusinessRole.Operator,
            isActive: false);
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Owner, "come-back");

        Assert.Equal("MembershipRevoked", await _factory.AccountStateOfAsync(ApiFactory.JoinerObjectId));

        var response = await _factory.Authenticated(ApiFactory.JoinerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Owner", (await ReadJsonAsync(response)).GetProperty("role").GetString());

        var membership = await _factory.MembershipAsync(ApiFactory.JoinerObjectId);
        Assert.Equal(membershipId, membership.Id);
        Assert.True(membership.IsActive);
        Assert.Equal(BusinessRole.Owner, membership.Role);
        Assert.True(membership.StatusChangedAtUtc > membership.CreatedAtUtc);

        // One row, not a second one beside the revoked original.
        Assert.Single(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
    }

    /// <summary>
    /// A redemption reaches the business whose link was presented, and not the caller's choice of
    /// one: the second business's invite makes the joiner a member of the second business. Nothing
    /// in the request identifies either.
    /// </summary>
    [Fact]
    public async Task The_invite_decides_which_business_is_joined()
    {
        await _factory.ResetAsync();
        await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "first-link");
        var token = await _factory.SeedInviteAsync(ApiFactory.OtherBusinessId, BusinessRole.Operator, "second-link");

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.JoinerObjectId).PostAsJsonAsync(Uri, new { token })).StatusCode);

        var membership = await _factory.MembershipAsync(ApiFactory.JoinerObjectId);
        Assert.Equal(ApiFactory.OtherBusinessId, membership.BusinessId);
        Assert.Equal(BusinessRole.Operator, membership.Role);

        // The other business's invite is untouched: being able to read a link is not using it.
        var untouched = Assert.Single(
            (await _factory.InvitesAsync()).Where(invite => invite.BusinessId == ApiFactory.BusinessId));
        Assert.Equal(MemberInviteStatus.Pending, untouched.Status);
    }

    #endregion

    #region One generic invalid-invite response

    /// <summary>
    /// Every unusable invite is the same <c>400</c> with the same sentence, so an unknown token
    /// cannot be told from a used, withdrawn, expired, foreign-business or undeclared-role one -
    /// which is what stops the endpoint confirming which links exist or that a business does. In
    /// every case nothing at all is written.
    /// </summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    [InlineData("undeclared-role")]
    [InlineData("pending-business")]
    [InlineData("deactivated-business")]
    [InlineData("rejected-business")]
    public async Task Every_unusable_invite_gets_the_one_generic_refusal_and_writes_nothing(string kind)
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedUnusableAsync(kind);

        var response = await _factory.Authenticated(ApiFactory.JoinerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadJsonAsync(response);
        Assert.Equal(MemberInviteRedemptionPolicy.InvalidInviteMessage, problem.GetProperty("detail").GetString());

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Vending Co", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Vending Co", body, StringComparison.Ordinal);

        // Nothing was written: no membership for the caller, and every invite left as it was.
        Assert.Empty(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
        Assert.All(
            await _factory.InvitesAsync(),
            invite => Assert.Null(invite.RedeemedByMembershipId));
        Assert.Equal("NoMembership", await _factory.AccountStateOfAsync(ApiFactory.JoinerObjectId));
    }

    /// <summary>
    /// A missing, blank or absent token is the same refusal as an unknown one. The endpoint
    /// deliberately does not report "the token field is required": a different answer for a
    /// malformed submission is still a different answer, and a caller probing for tokens learns
    /// from any difference at all.
    /// </summary>
    [Theory]
    [InlineData("""{"token":null}""")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":"   "}""")]
    [InlineData("{}")]
    public async Task A_missing_or_blank_token_gets_the_same_refusal(string body)
    {
        await _factory.ResetAsync();
        await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "a-live-link");

        var response = await _factory.Authenticated(ApiFactory.JoinerObjectId).PostAsync(
            Uri,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MemberInviteRedemptionPolicy.InvalidInviteMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.Empty(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
    }

    /// <summary>
    /// The token is matched exactly: a different case, extra whitespace, or a prefix of a real token
    /// is an unknown token. SHA-256 is case-sensitive and the comparison is on the digest, so this
    /// is pinning that nothing normalises a secret into a wider match.
    /// </summary>
    [Theory]
    [InlineData("A-KNOWN-TOKEN")]
    [InlineData(" a-known-token")]
    [InlineData("a-known-token ")]
    [InlineData("a-known-toke")]
    public async Task A_near_miss_token_is_an_unknown_token(string submitted)
    {
        await _factory.ResetAsync();
        await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "a-known-token");

        var response = await _factory.Authenticated(ApiFactory.JoinerObjectId)
            .PostAsJsonAsync(Uri, new { token = submitted });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
    }

    /// <summary>
    /// A single-use link is single-use through the pipeline too: the second redemption of the same
    /// token is the generic refusal, and the first person's membership is untouched.
    /// </summary>
    [Fact]
    public async Task The_same_link_cannot_be_redeemed_twice()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "one-use-only");

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.JoinerObjectId).PostAsJsonAsync(Uri, new { token })).StatusCode);

        var second = await _factory.Authenticated(ApiFactory.SecondJoinerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(
            MemberInviteRedemptionPolicy.InvalidInviteMessage,
            (await ReadJsonAsync(second)).GetProperty("detail").GetString());

        Assert.Single(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
        Assert.Empty(await _factory.MembershipsOfAsync(ApiFactory.SecondJoinerObjectId));
    }

    #endregion

    #region Eligibility

    /// <summary>
    /// A caller who already holds an active membership is refused with <c>409</c> and the standing
    /// membership sentence - one that names no business, because a person told they may not join is
    /// not thereby entitled to know which business they already belong to. Their existing access is
    /// untouched and the invite stays live.
    /// </summary>
    [Fact]
    public async Task An_existing_member_is_refused_with_a_conflict_and_keeps_their_own_access()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.OtherBusinessId, BusinessRole.Owner, "move-business");

        var response = await _factory.Authenticated(ApiFactory.ManagerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.DoesNotContain(
            "Vending Co",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var membership = await _factory.MembershipAsync(ApiFactory.ManagerObjectId);
        Assert.Equal(ApiFactory.BusinessId, membership.BusinessId);
        Assert.Equal(BusinessRole.Manager, membership.Role);

        Assert.All(
            await _factory.InvitesAsync(),
            invite => Assert.Equal(MemberInviteStatus.Pending, invite.Status));
    }

    /// <summary>
    /// The caller whose own business was deactivated gets the one refusal with its own sentence:
    /// they must first be released by the platform administrator, because a deactivated business has
    /// no reachable Owner who could revoke their membership. It still names no business.
    /// </summary>
    [Fact]
    public async Task A_caller_whose_business_was_deactivated_is_told_to_be_released_first()
    {
        await _factory.ResetAsync();
        await _factory.SetBusinessStatusAsync(ApiFactory.OtherBusinessId, BusinessStatus.Deactivated);
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "a-fresh-start");

        Assert.Equal("BusinessDeactivated", await _factory.AccountStateOfAsync(ApiFactory.OtherOwnerObjectId));

        var response = await _factory.Authenticated(ApiFactory.OtherOwnerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            MemberInviteRedemptionPolicy.ReleaseRequiredMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
        Assert.Contains("platform administrator", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Vending Co", body, StringComparison.Ordinal);

        Assert.All(
            await _factory.InvitesAsync(),
            invite => Assert.Equal(MemberInviteStatus.Pending, invite.Status));
    }

    /// <summary>
    /// A caller whose membership is in a business still awaiting its application decision is
    /// refused too: they hold an active membership, and the one-active-membership rule reads no
    /// business state at all.
    /// </summary>
    [Fact]
    public async Task A_caller_whose_business_is_still_pending_is_refused()
    {
        await _factory.ResetAsync();
        await _factory.SetBusinessStatusAsync(ApiFactory.OtherBusinessId, BusinessStatus.Pending);
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "somewhere-else");

        Assert.Equal("ApplicationPending", await _factory.AccountStateOfAsync(ApiFactory.OtherOwnerObjectId));

        var response = await _factory.Authenticated(ApiFactory.OtherOwnerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            MembershipEligibility.AlreadyAMemberMessage,
            (await ReadJsonAsync(response)).GetProperty("detail").GetString());
    }

    /// <summary>
    /// A rejected applicant may redeem: their membership record is inactive, so the one-active
    /// -membership rule admits them, and that is the state the issue lists as eligible.
    /// </summary>
    [Fact]
    public async Task A_rejected_applicant_may_redeem()
    {
        await _factory.ResetAsync();
        await _factory.SeedMembershipAsync(
            ApiFactory.OtherBusinessId,
            ApiFactory.JoinerObjectId,
            BusinessRole.Owner,
            isActive: false);
        await _factory.SetBusinessStatusAsync(ApiFactory.OtherBusinessId, BusinessStatus.Rejected);
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Operator, "second-chance");

        Assert.Equal("ApplicationRejected", await _factory.AccountStateOfAsync(ApiFactory.JoinerObjectId));

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.JoinerObjectId).PostAsJsonAsync(Uri, new { token })).StatusCode);

        // The new membership is in the inviting business, and the rejected record stays as it is.
        var memberships = await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId);
        Assert.Equal(2, memberships.Count);
        Assert.Single(memberships.Where(membership => membership.IsActive && membership.BusinessId == ApiFactory.BusinessId));
        Assert.Single(memberships.Where(membership => !membership.IsActive && membership.BusinessId == ApiFactory.OtherBusinessId));
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
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "needs-a-token");

        var response = await _factory.CreateClient().PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InvitesAsync())[0].Status);
    }

    /// <summary>
    /// The delegated <c>access_as_user</c> scope is required too, and its absence is the same
    /// <c>403</c> every other endpoint answers a missing scope with - the marker does not touch it.
    /// Nothing is written for such a request: the invite stays live and the caller joins nothing.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_delegated_scope_is_refused()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "needs-a-scope");

        var response = await _factory.WithoutScope(ApiFactory.JoinerObjectId)
            .PostAsJsonAsync(Uri, new { token });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await _factory.MembershipsOfAsync(ApiFactory.JoinerObjectId));
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InvitesAsync())[0].Status);
    }

    /// <summary>
    /// The exemption is this endpoint's alone. The same caller, with the same headers, reaches the
    /// redemption and is refused by everything else with the unchanged <c>403</c> - including the
    /// Owner-facing invite endpoints, which is the pairing that matters: being able to redeem an
    /// invite must not be a way to read or create one.
    /// </summary>
    [Fact]
    public async Task The_same_business_less_caller_reaches_nothing_else()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "only-this-one");
        var joiner = _factory.Authenticated(ApiFactory.JoinerObjectId);

        foreach (var forbidden in new[]
        {
            "/api/members/invites",
            "/api/members",
            "/api/me/access",
            "/api/business/current",
            "/api/Products",
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await joiner.GetAsync(forbidden)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await joiner.PostAsJsonAsync("/api/members/invites", new { role = "Owner" })).StatusCode);

        // And the one endpoint they may call still works.
        Assert.Equal(HttpStatusCode.OK, (await joiner.PostAsJsonAsync(Uri, new { token })).StatusCode);
    }

    /// <summary>
    /// The redemption runs with a denied business scope, so it reads no business data: the request
    /// that creates a membership cannot also see the business's products, sites or invites. This is
    /// asserted from the caller's side immediately after a successful redemption, with the same
    /// client - a scope that had been resolved would show up as those endpoints answering.
    /// </summary>
    [Fact]
    public async Task Redeeming_grants_access_only_from_the_next_request_onwards()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Owner, "then-a-member");
        var joiner = _factory.Authenticated(ApiFactory.JoinerObjectId);

        var response = await joiner.PostAsJsonAsync(Uri, new { token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Nothing about the business came back with the redemption itself.
        Assert.Equal("""{"role":"Owner"}""", await response.Content.ReadAsStringAsync());

        // The membership resolves from the next request on, which is where business data becomes
        // readable - through the ordinary tenant-scoped path, not through this endpoint.
        Assert.Equal(HttpStatusCode.OK, (await joiner.GetAsync("/api/me/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await joiner.GetAsync("/api/members/invites")).StatusCode);
    }

    #endregion

    #region Auditing

    /// <summary>
    /// One audit event per redemption, carrying the actor, the business and the invite - and neither
    /// the token nor any name. The assertions are over everything the host logged for the request,
    /// not one logger, because a secret reaching any category is the failure that matters.
    /// </summary>
    [Fact]
    public async Task A_redemption_is_audited_without_the_token_or_any_name()
    {
        await _factory.ResetAsync();
        var token = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, "audited-link");

        _factory.ClearLog();
        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.JoinerObjectId, "Dana Doe", "dana@example.test")
                .PostAsJsonAsync(Uri, new { token })).StatusCode);

        var invite = (await _factory.InvitesAsync())[0];
        var logged = _factory.LoggedText();

        Assert.Contains("Member invite audit.", logged, StringComparison.Ordinal);
        Assert.Contains(MemberInviteChange.Redeemed.ToString(), logged, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.JoinerObjectId, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"invite {invite.Id}", logged, StringComparison.Ordinal);
        Assert.Contains($"business {ApiFactory.BusinessId}", logged, StringComparison.Ordinal);

        Assert.DoesNotContain(token, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberInviteToken.Hash(token), logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dana Doe", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("dana@example.test", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused redemption is audited as nothing: a conflict and an invalid invite both changed
    /// nothing, and an audit trail that recorded them as redemptions would describe a history the
    /// database does not have. The refusals are still visible, as the boundary's own warnings.
    /// </summary>
    [Fact]
    public async Task A_refused_redemption_is_audited_as_nothing()
    {
        await _factory.ResetAsync();
        var live = await _factory.SeedInviteAsync(ApiFactory.OtherBusinessId, BusinessRole.Owner, "refused-link");

        _factory.ClearLog();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await _factory.Authenticated(ApiFactory.ManagerObjectId).PostAsJsonAsync(Uri, new { token = live }))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _factory.Authenticated(ApiFactory.JoinerObjectId).PostAsJsonAsync(Uri, new { token = "nothing" }))
                .StatusCode);

        var logged = _factory.LoggedText();
        Assert.DoesNotContain("Member invite audit.", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("refused-link", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing", logged, StringComparison.Ordinal);
    }

    #endregion

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// The API host over one in-memory SQLite connection, with the real migration history and two
    /// seeded businesses: the inviting one and a second whose Owner is used for the eligibility
    /// refusals.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "33333333-3333-3333-3333-333333333333";
        public const string OtherOwnerObjectId = "77777777-7777-7777-7777-777777777777";

        /// <summary>The person opening an invite link: no membership anywhere.</summary>
        public const string JoinerObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        /// <summary>A second such person, for the "same link twice" refusal.</summary>
        public const string SecondJoinerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;

        public static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly RecordingLoggerProvider _logs = new();

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId, string? displayName = null, string? email = null)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");

            if (displayName is not null)
            {
                client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.NameHeader, displayName);
            }

            if (email is not null)
            {
                client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.EmailHeader, email);
            }

            return client;
        }

        public HttpClient WithoutScope(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            return client;
        }

        public void ClearLog() => _logs.Clear();

        public string LoggedText() => _logs.Text();

        /// <summary>The account state the caller's own endpoint reports, as its published name.</summary>
        public async Task<string?> AccountStateOfAsync(string objectId)
        {
            var response = await Authenticated(objectId).GetAsync("/api/me/account-state");
            response.EnsureSuccessStatusCode();

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement
                .GetProperty("state")
                .GetString();
        }

        public async Task<MemberInviteRow> InviteAsync(int inviteId)
        {
            await using var db = Database();
            return await db.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == inviteId);
        }

        /// <summary>Every stored invite, of every business, in key order.</summary>
        public async Task<IReadOnlyList<MemberInviteRow>> InvitesAsync()
        {
            await using var db = Database();
            return await db.MemberInvites.AsNoTracking().OrderBy(invite => invite.Id).ToListAsync();
        }

        public async Task<BusinessMembership> MembershipAsync(string objectId)
        {
            await using var db = Database();
            return await db.BusinessMemberships
                .AsNoTracking()
                .SingleAsync(membership => membership.ObjectId == objectId && membership.IsActive);
        }

        /// <summary>Every membership row recorded for one identity, active or not.</summary>
        public async Task<IReadOnlyList<BusinessMembership>> MembershipsOfAsync(string objectId)
        {
            await using var db = Database();
            return await db.BusinessMemberships
                .AsNoTracking()
                .Where(membership => membership.ObjectId == objectId)
                .OrderBy(membership => membership.Id)
                .ToListAsync();
        }

        public async Task SetBusinessStatusAsync(int businessId, BusinessStatus status)
        {
            await using var db = Database();
            (await db.Businesses.SingleAsync(business => business.Id == businessId)).Status = status;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Writes one invite from outside the request boundary and returns its plaintext token, so a
        /// test can arrange an invite without going through the Owner's endpoint - including states
        /// the API will not create, such as an expired or already-used one.
        /// </summary>
        public async Task<string> SeedInviteAsync(
            int businessId,
            BusinessRole role,
            string token,
            MemberInviteStatus status = MemberInviteStatus.Pending,
            DateTime? expiresAtUtc = null,
            DateTime? usedAtUtc = null,
            DateTime? cancelledAtUtc = null)
        {
            await using var db = Database();
            var createdAtUtc = DateTime.UtcNow;
            db.MemberInvites.Add(new MemberInviteRow
            {
                BusinessId = businessId,
                Role = role,
                // Stored as the hash alone, exactly as a real creation stores it, so a test
                // presents what a person opening the link presents.
                TokenHash = MemberInviteToken.Hash(token),
                Status = status,
                CreatedByDirectoryTenantId = DirectoryTenantId,
                CreatedByObjectId = OwnerObjectId,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(createdAtUtc),
                UsedAtUtc = usedAtUtc,
                CancelledAtUtc = cancelledAtUtc,
            });
            await db.SaveChangesAsync();
            return token;
        }

        /// <summary>
        /// Arranges one of the ways an invite can be unusable, and returns the token to submit. They
        /// are collected here so the test above can assert that every one of them produces the same
        /// response, which is the property that matters.
        /// </summary>
        public async Task<string> SeedUnusableAsync(string kind)
        {
            var now = DateTime.UtcNow;

            switch (kind)
            {
                case "unknown":
                    // Nothing seeded: the token answers to no invite at all.
                    return "a-token-no-invite-answers-to";
                case "used":
                    return await SeedInviteAsync(
                        BusinessId, BusinessRole.Manager, "already-used",
                        MemberInviteStatus.Used, usedAtUtc: now);
                case "cancelled":
                    return await SeedInviteAsync(
                        BusinessId, BusinessRole.Manager, "withdrawn",
                        MemberInviteStatus.Cancelled, cancelledAtUtc: now);
                case "expired":
                    return await SeedInviteAsync(
                        BusinessId, BusinessRole.Manager, "ran-out",
                        expiresAtUtc: now.AddTicks(-1));
                case "undeclared-role":
                    return await SeedInviteAsync(OtherBusinessId, (BusinessRole)99, "a-role-nobody-declared");
                case "pending-business":
                    await SetBusinessStatusAsync(OtherBusinessId, BusinessStatus.Pending);
                    return await SeedInviteAsync(OtherBusinessId, BusinessRole.Manager, "not-approved-yet");
                case "deactivated-business":
                    await SetBusinessStatusAsync(OtherBusinessId, BusinessStatus.Deactivated);
                    return await SeedInviteAsync(OtherBusinessId, BusinessRole.Manager, "closed-down");
                case "rejected-business":
                    await SetBusinessStatusAsync(OtherBusinessId, BusinessStatus.Rejected);
                    return await SeedInviteAsync(OtherBusinessId, BusinessRole.Manager, "was-refused");
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown unusable-invite case.");
            }
        }

        public async Task<int> SeedMembershipAsync(
            int businessId,
            string objectId,
            BusinessRole role,
            bool isActive)
        {
            await using var db = Database();
            var membership = Membership(businessId, objectId, role);
            membership.IsActive = isActive;
            db.BusinessMemberships.Add(membership);
            await db.SaveChangesAsync();
            return membership.Id;
        }

        /// <summary>
        /// Puts the two businesses and their memberships back exactly as seeded, with no invite at
        /// all and nobody joined. These tests create memberships and close invites, so each one
        /// starts from the same world rather than from whatever its predecessor left behind.
        /// </summary>
        public async Task ResetAsync()
        {
            await using var db = Database();
            // Invites first: one may point at a membership, and that foreign key is restricted.
            db.MemberInvites.RemoveRange(await db.MemberInvites.ToListAsync());
            await db.SaveChangesAsync();
            db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
            await db.SaveChangesAsync();
            db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
            await db.SaveChangesAsync();

            db.Businesses.AddRange(
                Business(BusinessId, "Vending Co"),
                Business(OtherBusinessId, "Second Vending Co"));
            await db.SaveChangesAsync();

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(OtherBusinessId, OtherOwnerObjectId, BusinessRole.Owner));
            await db.SaveChangesAsync();
        }

        private static Business Business(int id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            };

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));

            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair, delegated scope and display claims
    /// come from request headers, so membership resolution, the scope requirement and issue #524's
    /// display values are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";
        public const string NameHeader = "X-Test-Name";
        public const string EmailHeader = "X-Test-Email";

        public TestActorAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new("tid", Request.Headers[DirectoryTenantIdHeader].ToString()),
                new("oid", objectId.ToString()),
            };
            if (Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                claims.Add(new Claim("scp", scope.ToString()));
            }

            if (Request.Headers.TryGetValue(NameHeader, out var name))
            {
                claims.Add(new Claim("name", name.ToString()));
            }

            if (Request.Headers.TryGetValue(EmailHeader, out var email))
            {
                claims.Add(new Claim("preferred_username", email.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }

    /// <summary>
    /// Captures everything the host logs, from every category and including log scopes, so the
    /// audit assertions are made over the whole request's logging rather than one logger's.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Clear()
        {
            lock (_entries)
            {
                _entries.Clear();
            }
        }

        public string Text()
        {
            lock (_entries)
            {
                return string.Join('\n', _entries);
            }
        }

        public void Dispose()
        {
        }

        private void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class Recording(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                provider.Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                provider.Add(formatter(state, exception));
                if (exception is not null)
                {
                    provider.Add(exception.ToString());
                }
            }
        }
    }
}
