using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Deactivating and reactivating a business, and releasing its members while it is off (issue #545),
/// over real HTTP.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>BusinessScopeMiddleware</c>'s independent
/// re-check of that policy, the tenant query filters and <c>BusinessOwnershipEnforcer</c> - so what
/// is under test is the real authorization and the real migrated schema. Only the authentication is
/// synthetic. Who may reach these four endpoints is asserted by
/// <see cref="SuperAdminBusinessesApiTests"/>, which calls every endpoint of the surface at once.</para>
/// </summary>
public class SuperAdminBusinessLifecycleApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string JoinableBusinessName = "Harbour Vending";
    private const string JoinableOwnerObjectId = "77777777-7777-7777-7777-777777777777";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminBusinessLifecycleApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Deactivating

    /// <summary>
    /// The criterion: one transition sets the status and cancels every live invite, the memberships
    /// keep their active flag and their state-change instant, nothing is deleted, and the business's
    /// members get the ordinary <c>403</c> on their next request.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_blocks_its_members_and_cancels_its_live_invites()
    {
        var live = await _factory.SeedInviteAsync(_factory.TradingBusinessId, "live-token-value");
        var membershipsBefore = await _factory.MembershipsOfAsync(_factory.TradingBusinessId);
        var owner = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/business/current")).StatusCode);

        var response = await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(nameof(BusinessStatus.Deactivated), body.RootElement.GetProperty("status").GetString());
        Assert.Equal(_factory.TradingBusinessId, body.RootElement.GetProperty("businessId").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("invitesCancelled").GetInt32());

        Assert.Equal(
            BusinessStatus.Deactivated,
            (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);

        // The invite is withdrawn: an outstanding link that still worked would be a way back into a
        // business whose door has just been closed.
        var invite = await _factory.InviteAsync(live);
        Assert.Equal(MemberInviteStatus.Cancelled, invite.Status);
        Assert.NotNull(invite.CancelledAtUtc);

        // No data is deleted and no membership is touched, flag and instant alike: the members are
        // refused because the business does not resolve, not because their rows were changed.
        Assert.Equal(
            membershipsBefore.Select(membership =>
                (membership.Id, membership.IsActive, membership.Role, membership.StatusChangedAtUtc)),
            (await _factory.MembershipsOfAsync(_factory.TradingBusinessId)).Select(membership =>
                (membership.Id, membership.IsActive, membership.Role, membership.StatusChangedAtUtc)));

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/products")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingManagerObjectId)
                .GetAsync("/api/products")).StatusCode);
    }

    /// <summary>
    /// The audit event: the actor, the business and the transition, with the count of invites the
    /// deactivation withdrew - and no business name, member name or email address anywhere in it.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_is_audited_once_without_any_name()
    {
        await _factory.SeedMembershipAsync(
            _factory.TradingBusinessId,
            "88888888-8888-8888-8888-888888888888",
            BusinessRole.Operator,
            isActive: true,
            displayName: "Dana Operator",
            email: "dana@vending.example.com");

        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();

        var audit = AuditEntry();
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"business {_factory.TradingBusinessId};", audit, StringComparison.Ordinal);
        Assert.Contains("Deactivated", audit, StringComparison.Ordinal);
        Assert.Contains("Active -> Deactivated", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dana", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dana@vending.example.com", audit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An invite that was already used, already cancelled or has expired is left exactly as it is: a
    /// cancellation records a deliberate decision at an instant, and rewriting a closed invite would
    /// make the record claim something that did not happen.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_leaves_invites_that_were_already_closed_alone()
    {
        var used = await _factory.SeedInviteAsync(
            _factory.TradingBusinessId,
            "used-token-value",
            status: MemberInviteStatus.Used);
        var cancelled = await _factory.SeedInviteAsync(
            _factory.TradingBusinessId,
            "cancelled-token-value",
            status: MemberInviteStatus.Cancelled);
        var expired = await _factory.SeedInviteAsync(
            _factory.TradingBusinessId,
            "expired-token-value",
            createdAtUtc: SuperAdminBusinessesApiFactory.CreatedAt,
            expiresAtUtc: SuperAdminBusinessesApiFactory.CreatedAt.AddDays(1));
        var otherBusinessInvite = await _factory.SeedInviteAsync(
            _factory.PendingBusinessId,
            "other-business-token-value");

        var response = await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(0, (await ReadJsonAsync(response)).RootElement.GetProperty("invitesCancelled").GetInt32());

        Assert.Equal(MemberInviteStatus.Used, (await _factory.InviteAsync(used)).Status);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.CreatedAt,
            (await _factory.InviteAsync(cancelled)).CancelledAtUtc);
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InviteAsync(expired)).Status);
        Assert.Null((await _factory.InviteAsync(expired)).CancelledAtUtc);

        // Another business's live invite is not this deactivation's business: the statement names
        // the business being switched off.
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InviteAsync(otherBusinessInvite)).Status);
    }

    /// <summary>
    /// Only a trading business may be deactivated. A pending application, a refused one and an
    /// already-deactivated business are each refused with the one sentence, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public async Task Only_an_active_business_may_be_deactivated(BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, status);
        var live = await _factory.SeedInviteAsync(_factory.TradingBusinessId, "live-token-value");

        var response = await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessLifecyclePolicy.NotActiveMessage, await DetailAsync(response));
        Assert.Equal(status, (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InviteAsync(live)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    #endregion

    #region Reactivating

    /// <summary>
    /// The criterion: reactivating restores only the memberships that were still active, leaves the
    /// revoked ones revoked, leaves the cancelled invites cancelled, and serves the business's Owner
    /// again.
    /// </summary>
    [Fact]
    public async Task Reactivating_restores_only_the_memberships_that_were_still_active()
    {
        var revoked = await _factory.SeedMembershipAsync(
            _factory.TradingBusinessId,
            "88888888-8888-8888-8888-888888888888",
            BusinessRole.Operator,
            isActive: false);
        var invite = await _factory.SeedInviteAsync(_factory.TradingBusinessId, "live-token-value");

        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();
        var released = await ReleaseAsync(_factory.TradingBusinessId, _factory.TradingManagerMembershipId);
        released.EnsureSuccessStatusCode();

        var response = await _factory.AsAdministrator().PostAsync(ReactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            nameof(BusinessStatus.Active),
            (await ReadJsonAsync(response)).RootElement.GetProperty("status").GetString());
        Assert.Equal(BusinessStatus.Active, (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);

        // The Owner was never released, so they are served again. The released Manager and the
        // membership that was revoked before any of this stay inactive: a reactivation restores the
        // business, never a membership.
        Assert.True((await _factory.MembershipAsync(_factory.TradingOwnerMembershipId)).IsActive);
        Assert.False((await _factory.MembershipAsync(_factory.TradingManagerMembershipId)).IsActive);
        Assert.False((await _factory.MembershipAsync(revoked)).IsActive);

        Assert.Equal(MemberInviteStatus.Cancelled, (await _factory.InviteAsync(invite)).Status);

        Assert.Equal(
            HttpStatusCode.OK,
            (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)
                .GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingManagerObjectId)
                .GetAsync("/api/business/current")).StatusCode);

        Assert.Contains("Deactivated -> Active", AuditEntries().Last(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion: once every Owner has been released, reactivating is refused with the sentence
    /// that says so, and nothing changes. A trading business nobody can administer has no repair
    /// path short of a human editing the database, which is why the refusal is part of the write
    /// rather than a warning afterwards.
    /// </summary>
    [Fact]
    public async Task Reactivating_a_business_with_no_active_owner_is_refused()
    {
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();
        (await ReleaseAsync(_factory.TradingBusinessId, _factory.TradingOwnerMembershipId))
            .EnsureSuccessStatusCode();
        _factory.ClearLog();

        var response = await _factory.AsAdministrator().PostAsync(ReactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessLifecyclePolicy.NoActiveOwnerMessage, await DetailAsync(response));
        Assert.Equal(
            BusinessStatus.Deactivated,
            (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));

        // The Manager is still an active membership, and it is deliberately not enough: only an
        // Owner may manage members, roles, the integration and the business record.
        Assert.True((await _factory.MembershipAsync(_factory.TradingManagerMembershipId)).IsActive);
    }

    /// <summary>
    /// Only a deactivated business may be reactivated. A trading business, a pending application and
    /// a refused one are each refused with the one sentence, and nothing changes - approving an
    /// application stays the decision of issue #544.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    public async Task Only_a_deactivated_business_may_be_reactivated(BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, status);

        var response = await _factory.AsAdministrator().PostAsync(ReactivateUrl(_factory.TradingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessLifecyclePolicy.NotDeactivatedMessage, await DetailAsync(response));
        Assert.Equal(status, (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    #endregion

    #region The members of a deactivated business

    /// <summary>
    /// The list reports the four declared fields and nothing else: the membership key the release
    /// takes, the display name so the administrator can tell the rows apart, the role, and whether
    /// the membership is active. No email address, no Entra identifier, and no business data.
    /// </summary>
    [Fact]
    public async Task The_members_list_reports_the_four_declared_fields_only()
    {
        await _factory.SeedMembershipAsync(
            _factory.TradingBusinessId,
            "88888888-8888-8888-8888-888888888888",
            BusinessRole.Operator,
            isActive: false,
            displayName: "Dana Operator",
            email: "dana@vending.example.com");
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();

        var response = await _factory.AsAdministrator().GetAsync(MembersUrl(_factory.TradingBusinessId));
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        var members = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        foreach (var member in members)
        {
            Assert.Equal(
                ["displayName", "isActive", "membershipId", "role"],
                member.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }

        var dana = members.Single(member => member.GetProperty("displayName").GetString() == "Dana Operator");
        Assert.Equal(nameof(BusinessRole.Operator), dana.GetProperty("role").GetString());
        Assert.False(dana.GetProperty("isActive").GetBoolean());

        var owner = members.Single(member =>
            member.GetProperty("membershipId").GetInt32() == _factory.TradingOwnerMembershipId);
        Assert.Equal(nameof(BusinessRole.Owner), owner.GetProperty("role").GetString());
        Assert.True(owner.GetProperty("isActive").GetBoolean());

        Assert.DoesNotContain("dana@vending.example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingOwnerObjectId,
            raw,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The criterion: releasing sets that membership inactive with its own state-change instant, and
    /// the last-Owner rule does not apply - which is the whole point, because a deactivated business
    /// has no reachable Owner to transfer the role to first.
    /// </summary>
    [Fact]
    public async Task Releasing_a_member_of_a_deactivated_business_records_the_instant()
    {
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();
        _factory.ClearLog();

        var response = await ReleaseAsync(_factory.TradingBusinessId, _factory.TradingOwnerMembershipId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(_factory.TradingOwnerMembershipId, body.RootElement.GetProperty("membershipId").GetInt32());
        Assert.False(body.RootElement.GetProperty("isActive").GetBoolean());

        var membership = await _factory.MembershipAsync(_factory.TradingOwnerMembershipId);
        Assert.False(membership.IsActive);
        Assert.NotEqual(SuperAdminBusinessesApiFactory.CreatedAt, membership.StatusChangedAtUtc);
        Assert.Equal(SuperAdminBusinessesApiFactory.CreatedAt, membership.CreatedAtUtc);
        Assert.Equal(BusinessRole.Owner, membership.Role);

        var audit = AuditEntry();
        Assert.Contains($"membership {_factory.TradingOwnerMembershipId}", audit, StringComparison.Ordinal);
        Assert.Contains("MemberReleased", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// Releasing a membership that is already inactive changes nothing and is audited as nothing: a
    /// row whose state did not change must not have its state-change instant rewritten, which would
    /// claim a transition that never happened (issue #522).
    /// </summary>
    [Fact]
    public async Task Releasing_an_already_inactive_membership_writes_nothing()
    {
        var revoked = await _factory.SeedMembershipAsync(
            _factory.TradingBusinessId,
            "88888888-8888-8888-8888-888888888888",
            BusinessRole.Operator,
            isActive: false);
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();
        _factory.ClearLog();

        var response = await ReleaseAsync(_factory.TradingBusinessId, revoked);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadJsonAsync(response)).RootElement.GetProperty("isActive").GetBoolean());

        var membership = await _factory.MembershipAsync(revoked);
        Assert.False(membership.IsActive);
        Assert.Equal(SuperAdminBusinessesApiFactory.CreatedAt, membership.StatusChangedAtUtc);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    /// <summary>
    /// The criterion: listing and releasing are both refused for any status other than deactivated,
    /// so a trading business's membership list stays its own Owner's business.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    public async Task The_members_of_a_business_that_is_not_deactivated_cannot_be_listed_or_released(
        BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, status);

        var listed = await _factory.AsAdministrator().GetAsync(MembersUrl(_factory.TradingBusinessId));
        var released = await ReleaseAsync(_factory.TradingBusinessId, _factory.TradingOwnerMembershipId);

        Assert.Equal(HttpStatusCode.Conflict, listed.StatusCode);
        Assert.Equal(BusinessLifecyclePolicy.MembersUnavailableMessage, await DetailAsync(listed));
        Assert.Equal(HttpStatusCode.Conflict, released.StatusCode);
        Assert.Equal(BusinessLifecyclePolicy.MembersUnavailableMessage, await DetailAsync(released));

        Assert.True((await _factory.MembershipAsync(_factory.TradingOwnerMembershipId)).IsActive);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    /// <summary>
    /// A membership key is only ever read and released together with the business it belongs to, so
    /// another business's member cannot be released by naming their key on this business's route.
    /// </summary>
    [Fact]
    public async Task A_membership_of_another_business_is_not_found_on_this_businesss_route()
    {
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();

        var response = await ReleaseAsync(_factory.TradingBusinessId, _factory.ApplicantMembershipId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((await _factory.MembershipAsync(_factory.ApplicantMembershipId)).IsActive);
    }

    /// <summary>
    /// The criterion, end to end and in order: a released person joins another business by invite,
    /// and reactivating the business they were released from leaves their released membership
    /// inactive, their new one active, and their account state <c>Member</c> of the new business.
    /// </summary>
    [Fact]
    public async Task A_released_person_joins_another_business_and_stays_released_when_theirs_returns()
    {
        var joinable = await _factory.SeedBusinessAsync(JoinableBusinessName, BusinessStatus.Active);
        await _factory.SeedMembershipAsync(joinable, JoinableOwnerObjectId, BusinessRole.Owner, isActive: true);
        const string Token = "harbour-invite-token-value";
        await _factory.SeedInviteAsync(joinable, Token, BusinessRole.Manager);

        var manager = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingManagerObjectId);

        // While the deactivated business still holds their membership they cannot join anywhere:
        // a person belongs to one business at a time, and only the platform administrator can free
        // this one.
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();
        var refused = await manager.PostAsJsonAsync("/api/invites/redeem", new { token = Token });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(MemberInviteRedemptionPolicy.ReleaseRequiredMessage, await DetailAsync(refused));

        (await ReleaseAsync(_factory.TradingBusinessId, _factory.TradingManagerMembershipId))
            .EnsureSuccessStatusCode();

        var redeemed = await manager.PostAsJsonAsync("/api/invites/redeem", new { token = Token });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal(
            nameof(BusinessRole.Manager),
            (await ReadJsonAsync(redeemed)).RootElement.GetProperty("role").GetString());

        (await _factory.AsAdministrator().PostAsync(ReactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();

        Assert.False((await _factory.MembershipAsync(_factory.TradingManagerMembershipId)).IsActive);
        var joined = Assert.Single(
            (await _factory.MembershipsOfAsync(joinable))
                .Where(membership => membership.ObjectId == SuperAdminBusinessesApiFactory.TradingManagerObjectId));
        Assert.True(joined.IsActive);
        Assert.Equal(BusinessRole.Manager, joined.Role);

        var accountState = await ReadJsonAsync(await manager.GetAsync("/api/me/account-state"));
        Assert.Equal(nameof(AccountState.Member), accountState.RootElement.GetProperty("state").GetString());

        var current = await ReadJsonAsync(await manager.GetAsync("/api/business/current"));
        Assert.Equal(JoinableBusinessName, current.RootElement.GetProperty("name").GetString());
    }

    #endregion

    #region Refusals the four endpoints share

    [Theory]
    [InlineData("deactivate")]
    [InlineData("reactivate")]
    public async Task An_unknown_business_is_not_found(string transition)
    {
        var response = await _factory.AsAdministrator()
            .PostAsync($"/api/super-admin/businesses/999999/{transition}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    [Fact]
    public async Task An_unknown_businesss_members_are_not_found()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(MembersUrl(999999))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync(ReleaseUrl(999999, _factory.TradingOwnerMembershipId), null)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_membership_of_a_deactivated_business_is_not_found()
    {
        (await _factory.AsAdministrator().PostAsync(DeactivateUrl(_factory.TradingBusinessId), null))
            .EnsureSuccessStatusCode();

        var response = await ReleaseAsync(_factory.TradingBusinessId, 999999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Helpers

    private static string DeactivateUrl(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/deactivate";

    private static string ReactivateUrl(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/reactivate";

    private static string MembersUrl(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/members";

    private static string ReleaseUrl(int businessId, int membershipId) =>
        $"{MembersUrl(businessId)}/{membershipId}/release";

    private Task<HttpResponseMessage> ReleaseAsync(int businessId, int membershipId) =>
        _factory.AsAdministrator().PostAsync(ReleaseUrl(businessId, membershipId), null);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("detail")
            .GetString();

    private static bool IsAuditEntry(string entry) =>
        entry.StartsWith("Platform business lifecycle audit.", StringComparison.Ordinal);

    private IReadOnlyList<string> AuditEntries() => [.. _factory.LoggedEntries().Where(IsAuditEntry)];

    /// <summary>The one lifecycle audit event the request under test emitted.</summary>
    private string AuditEntry() => Assert.Single(AuditEntries());

    #endregion
}
