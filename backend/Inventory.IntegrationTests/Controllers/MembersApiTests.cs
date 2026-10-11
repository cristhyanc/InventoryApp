using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The member management API of issue #533, exercised through the real request pipeline:
/// authentication, the required scope, the <c>Members.Manage</c> capability policy, the business
/// scope middleware, the controller and relational SQLite with the real migration history.
///
/// Three businesses are seeded throughout, which is what makes every assertion here meaningful: a
/// members list that could only have come from the caller's own membership, another business's
/// membership id that is simply not found, a business whose only Owner cannot remove themselves,
/// and a revoked member who has since joined somewhere else and so cannot be reinstated.
/// </summary>
public sealed class MembersApiTests : IClassFixture<MembersApiTests.ApiFactory>
{
    private const string Uri = "/api/members";

    private readonly ApiFactory _factory;

    public MembersApiTests(ApiFactory factory) => _factory = factory;

    private static string RoleUri(int membershipId) => $"{Uri}/{membershipId}/role";

    private static string RevokeUri(int membershipId) => $"{Uri}/{membershipId}/revoke";

    private static string ReinstateUri(int membershipId) => $"{Uri}/{membershipId}/reinstate";

    #region Listing

    /// <summary>
    /// The list an Owner sees: every membership of their own business, active and revoked alike,
    /// with the five display facts the agreed contract names and the membership id the change
    /// endpoints take. No other business's member appears, and nothing in the request chose which
    /// business was described.
    /// </summary>
    [Fact]
    public async Task An_owner_sees_every_member_of_their_own_business()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = (await ReadJsonAsync(response)).EnumerateArray().ToArray();

        Assert.Equal(
            ["displayName", "email", "isActive", "joinedAtUtc", "membershipId", "role"],
            members[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(
            [
                (ApiFactory.OwnerDisplayName, ApiFactory.OwnerEmail, "Owner", true),
                ("Grace Hopper", "grace@vending.example", "Owner", true),
                ("Mary Jackson", "mary@vending.example", "Manager", true),
                ("Alan Turing", "alan@vending.example", "Operator", true),
                ("Edsger Dijkstra", "edsger@vending.example", "Operator", false),
                ("Barbara Liskov", "barbara@vending.example", "Operator", false),
            ],
            members.Select(member => (
                member.GetProperty("displayName").GetString(),
                member.GetProperty("email").GetString(),
                member.GetProperty("role").GetString(),
                member.GetProperty("isActive").GetBoolean())));

        Assert.All(
            members,
            member => Assert.True(member.GetProperty("membershipId").GetInt32() > 0));
        Assert.All(
            members,
            member => Assert.Equal(
                ApiFactory.CreatedAt,
                member.GetProperty("joinedAtUtc").GetDateTime().ToUniversalTime()));
    }

    /// <summary>
    /// A member who has never signed in has no display name and no email address, and the list
    /// says so with an explicit <c>null</c> rather than an empty string or an object id: the two
    /// columns are display-only personal data refreshed from the member's own token (issue #524),
    /// and inventing a stand-in would present something that is not their name.
    /// </summary>
    [Fact]
    public async Task A_member_who_has_never_signed_in_is_listed_without_a_name_or_an_email()
    {
        await _factory.ResetAsync();

        var members = (await ReadJsonAsync(
                await _factory.Authenticated(ApiFactory.ThirdOwnerObjectId).GetAsync(Uri)))
            .EnumerateArray()
            .ToArray();

        var nameless = Assert.Single(
            members,
            member => member.GetProperty("role").GetString() == "Manager");
        Assert.Equal(JsonValueKind.Null, nameless.GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, nameless.GetProperty("email").ValueKind);
    }

    /// <summary>
    /// The second and third businesses are what make the list above an answer about one business:
    /// their own Owners see their own members and nothing of each other's.
    /// </summary>
    [Fact]
    public async Task The_list_never_carries_another_businesss_member()
    {
        await _factory.ResetAsync();

        var body = await (await _factory.Authenticated(ApiFactory.OtherOwnerObjectId).GetAsync(Uri))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain(ApiFactory.OwnerDisplayName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.OwnerEmail, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mary Jackson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("businessId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.DirectoryTenantId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.OwnerObjectId, body, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Authorization

    /// <summary>
    /// Owner-only, on every action. A Manager and an Operator hold no <c>Members.Manage</c>
    /// capability, so the authorization middleware refuses them before the controller runs, with a
    /// bare <c>403</c> that describes neither the capability nor their role.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    public async Task A_member_without_the_members_capability_is_refused_every_action(string objectId)
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(objectId);
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);

        foreach (var response in new[]
        {
            await client.GetAsync(Uri),
            await client.PutAsJsonAsync(RoleUri(target), new { role = "Operator" }),
            await client.PostAsync(RevokeUri(target), null),
            await client.PostAsync(ReinstateUri(target), null),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Members.Manage", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Owner", body, StringComparison.Ordinal);
        }

        // And the refusal really was a refusal: nothing about the target membership changed.
        var unchanged = await _factory.MembershipAsync(ApiFactory.OperatorObjectId);
        Assert.Equal(BusinessRole.Operator, unchanged.Role);
        Assert.True(unchanged.IsActive);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(Uri)).StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.WithoutScope(ApiFactory.OwnerObjectId).GetAsync(Uri)).StatusCode);
    }

    [Fact]
    public async Task A_caller_with_no_membership_is_refused_and_told_nothing()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiFactory.OwnerDisplayName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vending Co", body, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Two-business isolation

    /// <summary>
    /// The isolation rule, on each write: another business's membership id is not found, however
    /// complete the caller's own capability is. An Owner holds every capability there is and still
    /// reaches nothing of another business's, and the refusal is the boundary's <c>404</c> rather
    /// than anything that would confirm the id exists somewhere.
    /// </summary>
    [Fact]
    public async Task Another_businesss_membership_id_is_not_found_and_is_left_untouched()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);
        var foreign = await _factory.MembershipIdOfAsync(ApiFactory.OtherOwnerObjectId);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(RoleUri(foreign), new { role = "Operator" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(RevokeUri(foreign), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(ReinstateUri(foreign), null)).StatusCode);

        var untouched = await _factory.MembershipAsync(ApiFactory.OtherOwnerObjectId);
        Assert.Equal(BusinessRole.Owner, untouched.Role);
        Assert.True(untouched.IsActive);
    }

    [Fact]
    public async Task A_membership_id_that_exists_nowhere_is_not_found()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(RoleUri(999_999), new { role = "Operator" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(RevokeUri(999_999), null)).StatusCode);
    }

    #endregion

    #region Changing a role

    /// <summary>
    /// A role change takes effect on the target's next request, and not before: membership
    /// resolution is memoised per request only, so there is no cache to wait for and no sign-out
    /// needed.
    /// </summary>
    [Fact]
    public async Task A_changed_role_applies_on_the_targets_next_request()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);
        var targetClient = _factory.Authenticated(ApiFactory.OperatorObjectId);

        Assert.Equal(
            "Operator",
            (await ReadJsonAsync(await targetClient.GetAsync("/api/me/access")))
                .GetProperty("role").GetString());

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(target), new { role = "Manager" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await ReadJsonAsync(response);
        Assert.Equal("Manager", changed.GetProperty("role").GetString());
        Assert.Equal(target, changed.GetProperty("membershipId").GetInt32());
        Assert.True(changed.GetProperty("isActive").GetBoolean());

        Assert.Equal(
            "Manager",
            (await ReadJsonAsync(await targetClient.GetAsync("/api/me/access")))
                .GetProperty("role").GetString());
    }

    /// <summary>
    /// An unsupported role is refused before anything is stored. An enum is a compile-time
    /// constraint, not a validation: a JSON string and a hand-built request both submit arbitrary
    /// text. A numeric spelling is refused with the rest, so a client cannot supply a stored
    /// representation, and so is <c>Viewer</c> - the read-only role #328 anticipates but this
    /// deployment does not declare.
    /// </summary>
    [Theory]
    [InlineData("Administrator")]
    [InlineData("Viewer")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("40")]
    [InlineData(null)]
    public async Task An_unsupported_role_is_rejected_and_changes_nothing(string? role)
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(target), new { role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BusinessRole.Operator, (await _factory.MembershipAsync(ApiFactory.OperatorObjectId)).Role);
    }

    /// <summary>
    /// A declared role name is accepted whatever case it is written in, and with the surrounding
    /// whitespace a client may send: the vocabulary is the name, not its spelling, and
    /// <c>BusinessRoles.TryParse</c> is the same check <c>bootstrap-business</c> applies to a
    /// configured role.
    /// </summary>
    [Fact]
    public async Task A_declared_role_name_is_accepted_case_insensitively()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(target), new { role = " mAnAgEr " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BusinessRole.Manager, (await _factory.MembershipAsync(ApiFactory.OperatorObjectId)).Role);
    }

    /// <summary>
    /// A revoked membership's role is history and is not edited in place. Reinstating restores the
    /// role the row holds, so quietly rewriting it would queue up access nobody reviewed for a
    /// later reinstatement; the Owner reinstates first and then changes the role, and both steps
    /// are audited.
    /// </summary>
    [Fact]
    public async Task A_revoked_memberships_role_cannot_be_changed()
    {
        await _factory.ResetAsync();
        var revoked = await _factory.MembershipIdOfAsync(ApiFactory.RevokedObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(revoked), new { role = "Manager" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await _factory.MembershipAsync(ApiFactory.RevokedObjectId);
        Assert.Equal(BusinessRole.Operator, stored.Role);
        Assert.False(stored.IsActive);
    }

    /// <summary>
    /// Setting the role a member already holds is accepted and writes nothing, so a client that
    /// replays a change does not produce a second audit event for a change that did not happen.
    /// </summary>
    [Fact]
    public async Task Setting_the_role_a_member_already_holds_changes_nothing()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.ManagerObjectId);
        var before = await _factory.MembershipAsync(ApiFactory.ManagerObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(target), new { role = "Manager" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await _factory.MembershipAsync(ApiFactory.ManagerObjectId);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.StatusChangedAtUtc, after.StatusChangedAtUtc);
    }

    #endregion

    #region Revoking and reinstating

    /// <summary>
    /// Revoking stops the member's access on their next request, and keeps the row: the approval
    /// stays visible as revoked rather than being deleted, which is also what lets the person be
    /// reinstated or join another business later.
    /// </summary>
    [Fact]
    public async Task Revoking_a_member_stops_their_access_on_their_next_request()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);
        var targetClient = _factory.Authenticated(ApiFactory.OperatorObjectId);

        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/me/access")).StatusCode);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(RevokeUri(target), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadJsonAsync(response)).GetProperty("isActive").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync("/api/me/access")).StatusCode);

        var stored = await _factory.MembershipAsync(ApiFactory.OperatorObjectId);
        Assert.False(stored.IsActive);
        Assert.Equal(BusinessRole.Operator, stored.Role);
        Assert.True(stored.StatusChangedAtUtc > stored.CreatedAtUtc);
    }

    [Fact]
    public async Task Revoking_an_already_revoked_membership_changes_nothing()
    {
        await _factory.ResetAsync();
        var revoked = await _factory.MembershipIdOfAsync(ApiFactory.RevokedObjectId);
        var before = await _factory.MembershipAsync(ApiFactory.RevokedObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(RevokeUri(revoked), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            before.StatusChangedAtUtc,
            (await _factory.MembershipAsync(ApiFactory.RevokedObjectId)).StatusChangedAtUtc);
    }

    /// <summary>
    /// Reinstating restores the role the revoked row holds, and the member can work again on their
    /// next request.
    /// </summary>
    [Fact]
    public async Task Reinstating_a_revoked_member_restores_their_access()
    {
        await _factory.ResetAsync();
        var revoked = await _factory.MembershipIdOfAsync(ApiFactory.RevokedObjectId);
        var targetClient = _factory.Authenticated(ApiFactory.RevokedObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync("/api/me/access")).StatusCode);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(ReinstateUri(revoked), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reinstated = await ReadJsonAsync(response);
        Assert.True(reinstated.GetProperty("isActive").GetBoolean());
        Assert.Equal("Operator", reinstated.GetProperty("role").GetString());

        Assert.Equal(
            "Operator",
            (await ReadJsonAsync(await targetClient.GetAsync("/api/me/access")))
                .GetProperty("role").GetString());
    }

    /// <summary>
    /// The reinstatement rule of the agreed contract: a person who has since gained an active
    /// membership somewhere else is refused with <c>409</c>. A person belongs to one business at a
    /// time, and reinstating them here would deny them both. The message names no business, member
    /// or role - the Owner being told this is not thereby entitled to know where that person went.
    /// </summary>
    [Fact]
    public async Task Reinstating_a_member_who_now_belongs_elsewhere_is_refused_with_409()
    {
        await _factory.ResetAsync();
        var revoked = await _factory.MembershipIdOfAsync(ApiFactory.MovedOnObjectId, ApiFactory.BusinessId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(ReinstateUri(revoked), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(MembershipEligibility.AlreadyAMemberMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.OtherBusinessName, body, StringComparison.OrdinalIgnoreCase);

        // Refused and rolled back: the row is still revoked here, and still active there.
        Assert.False((await _factory.MembershipAsync(ApiFactory.MovedOnObjectId, ApiFactory.BusinessId)).IsActive);
        Assert.True((await _factory.MembershipAsync(ApiFactory.MovedOnObjectId, ApiFactory.OtherBusinessId)).IsActive);
    }

    [Fact]
    public async Task Reinstating_an_already_active_membership_changes_nothing()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.ManagerObjectId);
        var before = await _factory.MembershipAsync(ApiFactory.ManagerObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(ReinstateUri(target), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            before.StatusChangedAtUtc,
            (await _factory.MembershipAsync(ApiFactory.ManagerObjectId)).StatusChangedAtUtc);
    }

    #endregion

    #region The last active Owner

    /// <summary>
    /// The business always keeps someone who can administer it. The third business has one Owner
    /// and one Manager, and that Owner may neither revoke nor demote themselves: only an Owner may
    /// manage members, roles, the integration and the business record, so a business that loses its
    /// last active Owner cannot be administered by anybody inside it and has no repair path short
    /// of a human editing the database.
    /// </summary>
    [Fact]
    public async Task The_last_active_owner_cannot_revoke_themselves()
    {
        await _factory.ResetAsync();
        var self = await _factory.MembershipIdOfAsync(ApiFactory.ThirdOwnerObjectId);

        var response = await _factory.Authenticated(ApiFactory.ThirdOwnerObjectId)
            .PostAsync(RevokeUri(self), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            BusinessOwnerRetention.LastActiveOwnerMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var stored = await _factory.MembershipAsync(ApiFactory.ThirdOwnerObjectId);
        Assert.True(stored.IsActive);
        Assert.Equal(BusinessRole.Owner, stored.Role);
    }

    [Fact]
    public async Task The_last_active_owner_cannot_demote_themselves()
    {
        await _factory.ResetAsync();
        var self = await _factory.MembershipIdOfAsync(ApiFactory.ThirdOwnerObjectId);

        var response = await _factory.Authenticated(ApiFactory.ThirdOwnerObjectId)
            .PutAsJsonAsync(RoleUri(self), new { role = "Manager" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessRole.Owner, (await _factory.MembershipAsync(ApiFactory.ThirdOwnerObjectId)).Role);
    }

    /// <summary>
    /// The rule is about the state the business is left in, not about who performed the change: an
    /// Owner may step down, and may be revoked, while another active Owner remains.
    /// </summary>
    [Fact]
    public async Task An_owner_may_step_down_while_another_active_owner_remains()
    {
        await _factory.ResetAsync();
        var self = await _factory.MembershipIdOfAsync(ApiFactory.OwnerObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PutAsJsonAsync(RoleUri(self), new { role = "Manager" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BusinessRole.Manager, (await _factory.MembershipAsync(ApiFactory.OwnerObjectId)).Role);

        // And having stepped down they may no longer manage members: the change applied on the
        // next request, including their own.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri)).StatusCode);
    }

    /// <summary>
    /// A revoked Owner is not an Owner. Revoking the business's only <em>active</em> Owner is
    /// refused even while a revoked Owner row exists, because a revoked approval administers
    /// nothing.
    /// </summary>
    [Fact]
    public async Task A_revoked_owner_does_not_satisfy_the_last_owner_rule()
    {
        await _factory.ResetAsync();
        await _factory.RevokeOutsideTheBoundaryAsync(ApiFactory.SecondOwnerObjectId);
        var self = await _factory.MembershipIdOfAsync(ApiFactory.OwnerObjectId);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsync(RevokeUri(self), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await _factory.MembershipAsync(ApiFactory.OwnerObjectId)).IsActive);
    }

    #endregion

    #region Audit

    /// <summary>
    /// One audit event per change, carrying the actor, the business, the membership and the old →
    /// new role and status - and none of the display-only personal data the membership row also
    /// holds. An audit trail that cannot say who changed what is not an audit trail; a name and an
    /// email address are not what identifies anybody here, and issue #524 made never logging them
    /// a standing rule.
    /// </summary>
    [Fact]
    public async Task A_change_is_audited_without_any_name_or_email()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId);
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.OwnerObjectId)
                .PutAsJsonAsync(RoleUri(target), new { role = "Manager" })).StatusCode);

        var logged = _factory.LoggedText();
        Assert.Contains("Member management audit", logged, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.OwnerObjectId, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ApiFactory.DirectoryTenantId, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"membership {target}", logged, StringComparison.Ordinal);
        Assert.Contains("Operator", logged, StringComparison.Ordinal);
        Assert.Contains("Manager", logged, StringComparison.Ordinal);

        foreach (var personal in ApiFactory.EveryNameAndEmail)
        {
            Assert.DoesNotContain(personal, logged, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A refused change is not an applied one, so nothing claims it happened. The refusal is still
    /// visible: the conflict the boundary logs is the record of the attempt.
    /// </summary>
    [Fact]
    public async Task A_refused_change_is_not_audited_as_a_change()
    {
        await _factory.ResetAsync();
        var self = await _factory.MembershipIdOfAsync(ApiFactory.ThirdOwnerObjectId);
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await _factory.Authenticated(ApiFactory.ThirdOwnerObjectId).PostAsync(RevokeUri(self), null)).StatusCode);

        Assert.DoesNotContain("Member management audit", _factory.LoggedText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A change that changed nothing is not audited either: the audit records state transitions,
    /// and a replayed request produced none.
    /// </summary>
    [Fact]
    public async Task A_change_that_changed_nothing_is_not_audited()
    {
        await _factory.ResetAsync();
        var target = await _factory.MembershipIdOfAsync(ApiFactory.ManagerObjectId);
        _factory.ClearLog();

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(ApiFactory.OwnerObjectId)
                .PutAsJsonAsync(RoleUri(target), new { role = "Manager" })).StatusCode);

        Assert.DoesNotContain("Member management audit", _factory.LoggedText(), StringComparison.Ordinal);
    }

    #endregion

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string SecondOwnerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string ManagerObjectId = "33333333-3333-3333-3333-333333333333";
        public const string OperatorObjectId = "44444444-4444-4444-4444-444444444444";
        public const string RevokedObjectId = "55555555-5555-5555-5555-555555555555";
        public const string MovedOnObjectId = "66666666-6666-6666-6666-666666666666";
        public const string OtherOwnerObjectId = "77777777-7777-7777-7777-777777777777";
        public const string ThirdOwnerObjectId = "88888888-8888-8888-8888-888888888888";
        public const string ThirdManagerObjectId = "99999999-aaaa-bbbb-cccc-dddddddddddd";
        public const string StrangerObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        public const string OwnerDisplayName = "Ada Lovelace";
        public const string OwnerEmail = "ada@vending.example";
        public const string OtherBusinessName = "Second Vending Co";

        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;
        public const int ThirdBusinessId = 3;

        public static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Every display value the seed stores, so the audit test can assert that none of them
        /// reached a log rather than naming one of them and hoping.
        /// </summary>
        public static readonly string[] EveryNameAndEmail =
        [
            OwnerDisplayName,
            OwnerEmail,
            "Grace Hopper",
            "grace@vending.example",
            "Mary Jackson",
            "mary@vending.example",
            "Alan Turing",
            "alan@vending.example",
            "Edsger Dijkstra",
            "edsger@vending.example",
            "Barbara Liskov",
            "barbara@vending.example",
            "Katherine Johnson",
            "katherine@second.example",
            "Hedy Lamarr",
            "hedy@third.example",
        ];

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

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
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

        public async Task<int> MembershipIdOfAsync(string objectId, int? businessId = null) =>
            (await MembershipAsync(objectId, businessId)).Id;

        public async Task<BusinessMembership> MembershipAsync(string objectId, int? businessId = null)
        {
            await using var db = Database();
            var rows = db.BusinessMemberships.AsNoTracking().Where(membership => membership.ObjectId == objectId);
            if (businessId is { } owner)
            {
                rows = rows.Where(membership => membership.BusinessId == owner);
            }

            return await rows.SingleAsync();
        }

        /// <summary>
        /// Revokes a membership from outside the request boundary, so a test can arrange a state
        /// the API itself would refuse to create.
        /// </summary>
        public async Task RevokeOutsideTheBoundaryAsync(string objectId)
        {
            await using var db = Database();
            var membership = await db.BusinessMemberships.SingleAsync(row => row.ObjectId == objectId);
            membership.IsActive = false;
            membership.StatusChangedAtUtc = CreatedAt;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Puts the three businesses and their memberships back exactly as seeded. These tests
        /// change membership state, so each one starts from the same world rather than from
        /// whatever its predecessor left behind.
        /// </summary>
        public async Task ResetAsync()
        {
            await using var db = Database();
            db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
            await db.SaveChangesAsync();
            db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
            await db.SaveChangesAsync();

            db.Businesses.AddRange(
                Business(BusinessId, "Vending Co"),
                Business(OtherBusinessId, OtherBusinessName),
                Business(ThirdBusinessId, "Third Vending Co"));
            await db.SaveChangesAsync();

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner, OwnerDisplayName, OwnerEmail),
                Membership(BusinessId, SecondOwnerObjectId, BusinessRole.Owner, "Grace Hopper", "grace@vending.example"),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager, "Mary Jackson", "mary@vending.example"),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator, "Alan Turing", "alan@vending.example"),
                Membership(
                    BusinessId,
                    RevokedObjectId,
                    BusinessRole.Operator,
                    "Edsger Dijkstra",
                    "edsger@vending.example",
                    isActive: false),
                // Revoked here and active in the second business: the person who moved on, whom
                // this business may no longer reinstate.
                Membership(
                    BusinessId,
                    MovedOnObjectId,
                    BusinessRole.Operator,
                    "Barbara Liskov",
                    "barbara@vending.example",
                    isActive: false),
                Membership(
                    OtherBusinessId,
                    OtherOwnerObjectId,
                    BusinessRole.Owner,
                    "Katherine Johnson",
                    "katherine@second.example"),
                Membership(
                    OtherBusinessId,
                    MovedOnObjectId,
                    BusinessRole.Operator,
                    "Barbara Liskov",
                    "barbara@vending.example"),
                // The third business has exactly one active Owner, and a Manager who has never
                // signed in and so has neither display value stored.
                Membership(ThirdBusinessId, ThirdOwnerObjectId, BusinessRole.Owner, "Hedy Lamarr", "hedy@third.example"),
                Membership(ThirdBusinessId, ThirdManagerObjectId, BusinessRole.Manager, null, null));

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

        private static BusinessMembership Membership(
            int businessId,
            string objectId,
            BusinessRole role,
            string? displayName,
            string? email,
            bool isActive = true) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                DisplayName = displayName,
                Email = email,
                Role = role,
                IsActive = isActive,
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
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the capability policy and the scope requirement
    /// are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";

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
