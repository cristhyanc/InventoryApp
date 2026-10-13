using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin businesses API over real HTTP (issue #544): who may reach it, what it reports,
/// what each decision writes, and what it refuses.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>,
/// the platform-admin policy, <c>[RequiredScope]</c>, <c>BusinessScopeMiddleware</c>'s independent
/// re-check of the same policy, the tenant query filters and <c>BusinessOwnershipEnforcer</c> - so
/// the authorization under test is the real one. Only the authentication is synthetic.</para>
/// </summary>
public class SuperAdminBusinessesApiTests : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminBusinessesApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The acceptance criterion "non-admins (including Owners) denied", asked of every endpoint on
    /// the surface. An Owner holds the strongest business role there is and still reaches none of
    /// it: a business role is not platform authority, and this controller declares no capability any
    /// role could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c> by the business-scope middleware: "you are not signed in" and "you are signed in
    /// and not the administrator" must stay different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// The delegated scope is required of the administrator too. Platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// The bypass is of the membership requirement only. The administrator has no membership
    /// anywhere, so every business endpoint still refuses them - reaching this surface grants
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_reaches_no_business_endpoint()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/members")).StatusCode);
    }

    #endregion

    #region The directory

    /// <summary>
    /// The list: every business, pending applications first, with the ten declared fields and the
    /// application detail read off the rows.
    /// </summary>
    [Fact]
    public async Task The_directory_lists_every_business_with_pending_applications_first()
    {
        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(ListUrl));

        var businesses = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, businesses.Count);

        Assert.Equal(nameof(BusinessStatus.Pending), businesses[0].GetProperty("status").GetString());
        Assert.Equal(SuperAdminBusinessesApiFactory.PendingBusinessName, businesses[0].GetProperty("name").GetString());
        Assert.Equal(_factory.PendingBusinessId, businesses[0].GetProperty("businessId").GetInt32());
        Assert.Equal(
            SuperAdminBusinessesApiFactory.PendingContactEmail,
            businesses[0].GetProperty("contactEmail").GetString());
        Assert.Equal(SuperAdminBusinessesApiFactory.PendingAbn, businesses[0].GetProperty("abn").GetString());
        Assert.Equal(1, businesses[0].GetProperty("activeMemberCount").GetInt32());
        Assert.False(businesses[0].GetProperty("nayaxConfigured").GetBoolean());
        Assert.Equal(
            SuperAdminBusinessesApiFactory.AppliedAt,
            businesses[0].GetProperty("appliedAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, businesses[0].GetProperty("decidedAtUtc").ValueKind);

        var trading = businesses.Single(business =>
            business.GetProperty("businessId").GetInt32() == _factory.TradingBusinessId);
        Assert.Equal(2, trading.GetProperty("activeMemberCount").GetInt32());

        var rejected = businesses.Single(business =>
            business.GetProperty("businessId").GetInt32() == _factory.RejectedBusinessId);
        Assert.Equal(0, rejected.GetProperty("activeMemberCount").GetInt32());
        Assert.Equal(nameof(BusinessStatus.Rejected), rejected.GetProperty("status").GetString());
    }

    /// <summary>
    /// The acceptance criterion "no tenant data in responses", asserted structurally and by value:
    /// the response objects carry exactly the ten declared properties, and a seeded product name and
    /// Nayax token ciphertext appear nowhere in the body - while the connection's existence is
    /// still reported as the yes/no it is meant to be.
    /// </summary>
    [Fact]
    public async Task The_directory_reports_no_tenant_data_and_no_credential()
    {
        const string ProductName = "Salted Caramel 40g";
        const string Ciphertext = "ciphertext-marker-that-must-never-be-served";
        await _factory.SeedTenantDataAsync(ProductName, Ciphertext);

        var response = await _factory.AsAdministrator().GetAsync(ListUrl);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        Assert.DoesNotContain(ProductName, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Ciphertext, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("2002736764", raw, StringComparison.Ordinal);

        foreach (var business in body.RootElement.EnumerateArray())
        {
            Assert.Equal(
                [
                    "abn",
                    "activeMemberCount",
                    "appliedAtUtc",
                    "businessId",
                    "contactEmail",
                    "createdAtUtc",
                    "decidedAtUtc",
                    "name",
                    "nayaxConfigured",
                    "status",
                ],
                business.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }

        Assert.True(
            body.RootElement.EnumerateArray()
                .Single(business => business.GetProperty("businessId").GetInt32() == _factory.TradingBusinessId)
                .GetProperty("nayaxConfigured")
                .GetBoolean());
    }

    [Fact]
    public async Task The_pending_count_reports_the_applications_waiting_for_a_decision()
    {
        var body = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PendingCountUrl));

        Assert.Equal(1, body.RootElement.GetProperty("pendingCount").GetInt32());

        await _factory.SetBusinessStatusAsync(_factory.RejectedBusinessId, BusinessStatus.Pending);

        var again = await ReadJsonAsync(await _factory.AsAdministrator().GetAsync(PendingCountUrl));
        Assert.Equal(2, again.RootElement.GetProperty("pendingCount").GetInt32());
    }

    #endregion

    #region Approving

    /// <summary>
    /// The transition: the business becomes <c>Active</c> with a decision instant, the applicant
    /// keeps the active Owner membership that made the application theirs, and the audit event
    /// records the decision without a name, an address or an applicant.
    /// </summary>
    [Fact]
    public async Task Approving_a_pending_application_activates_the_business_and_is_audited()
    {
        var response = await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(nameof(BusinessStatus.Active), body.RootElement.GetProperty("status").GetString());
        Assert.Equal(_factory.PendingBusinessId, body.RootElement.GetProperty("businessId").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("rejectionReason").ValueKind);

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, business.Status);
        Assert.NotNull(business.DecidedAtUtc);
        Assert.Equal(SuperAdminBusinessesApiFactory.AppliedAt, business.AppliedAtUtc);
        Assert.Null(business.RejectionReason);

        var applicant = await _factory.MembershipAsync(_factory.ApplicantMembershipId);
        Assert.True(applicant.IsActive);
        Assert.Equal(BusinessRole.Owner, applicant.Role);

        // Case-insensitively: ActorIdentity stores the canonical upper-case spelling of a GUID, so
        // the audit records the identity rather than the characters the header happened to carry.
        var audit = AuditEntry();
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"business {_factory.PendingBusinessId};", audit, StringComparison.Ordinal);
        Assert.Contains("Pending -> Active", audit, StringComparison.Ordinal);
        Assert.Contains("reason recorded False", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.PendingBusinessName,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.PendingContactEmail,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.ApplicantObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The approved business's Owner is served, which is the whole point of the decision: before it
    /// their own business endpoint refuses them, and afterwards it answers.
    /// </summary>
    [Fact]
    public async Task The_applicant_reaches_their_business_only_once_it_is_approved()
    {
        var applicant = _factory.Authenticated(SuperAdminBusinessesApiFactory.ApplicantObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await applicant.GetAsync("/api/business/current")).StatusCode);

        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();

        var current = await ReadJsonAsync(await applicant.GetAsync("/api/business/current"));
        Assert.Equal(
            SuperAdminBusinessesApiFactory.PendingBusinessName,
            current.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// The acceptance criterion "approve after the applicant joined elsewhere": their membership in
    /// the application is no longer active and they hold an active one in another business, so the
    /// approval is refused and nothing changes.
    /// </summary>
    [Fact]
    public async Task Approving_is_refused_once_the_applicant_has_joined_another_business()
    {
        await _factory.SetMembershipActiveAsync(_factory.ApplicantMembershipId, isActive: false);
        await using (var db = _factory.Database())
        {
            db.BusinessMemberships.Add(new Inventory.Infrastructure.Models.BusinessMembership
            {
                BusinessId = _factory.TradingBusinessId,
                DirectoryTenantId = SuperAdminBusinessesApiFactory.DirectoryTenantId,
                ObjectId = SuperAdminBusinessesApiFactory.ApplicantObjectId,
                Role = BusinessRole.Operator,
                IsActive = true,
                CreatedAtUtc = SuperAdminBusinessesApiFactory.CreatedAt,
                StatusChangedAtUtc = SuperAdminBusinessesApiFactory.CreatedAt,
            });
            await db.SaveChangesAsync();
        }

        var response = await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            BusinessDecisionPolicy.ApplicantUnavailableMessage,
            await DetailAsync(response));
        Assert.Equal(
            BusinessStatus.Pending,
            (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    #endregion

    #region Refusing

    /// <summary>
    /// The transition: the business becomes <c>Rejected</c> with the reason and a decision instant,
    /// and the applicant's membership is released so they are free to apply again.
    /// </summary>
    [Fact]
    public async Task Rejecting_a_pending_application_records_the_reason_and_releases_the_applicant()
    {
        var response = await _factory.AsAdministrator()
            .PostAsJsonAsync(RejectUrl(_factory.PendingBusinessId), new { reason = "  Needs a registered ABN.  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(nameof(BusinessStatus.Rejected), body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Needs a registered ABN.", body.RootElement.GetProperty("rejectionReason").GetString());

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Equal("Needs a registered ABN.", business.RejectionReason);
        Assert.NotNull(business.DecidedAtUtc);

        var applicant = await _factory.MembershipAsync(_factory.ApplicantMembershipId);
        Assert.False(applicant.IsActive);
        Assert.Equal(business.DecidedAtUtc, applicant.StatusChangedAtUtc);

        var audit = AuditEntry();
        Assert.Contains("Pending -> Rejected", audit, StringComparison.Ordinal);
        Assert.Contains("reason recorded True", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("registered ABN", audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection needs no body at all, and no reason. "No reason" has one stored representation,
    /// so an absent body, an absent field and a blank one are the same rejection.
    /// </summary>
    [Fact]
    public async Task Rejecting_without_a_body_is_still_a_rejection()
    {
        var response = await _factory.AsAdministrator().PostAsync(RejectUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            (await ReadJsonAsync(response)).RootElement.GetProperty("rejectionReason").ValueKind);

        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Null(business.RejectionReason);
        Assert.False((await _factory.MembershipAsync(_factory.ApplicantMembershipId)).IsActive);
        Assert.Contains("reason recorded False", AuditEntry(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An over-long reason is refused with <c>400</c> and writes nothing: a reason nothing could
    /// store must not be silently truncated into the applicant's own explanation.
    /// </summary>
    [Fact]
    public async Task A_reason_longer_than_the_limit_is_refused_and_writes_nothing()
    {
        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            RejectUrl(_factory.PendingBusinessId),
            new { reason = new string('x', BusinessDecisionPolicy.MaxRejectionReasonLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BusinessDecisionPolicy.RejectionReasonTooLongMessage, await DetailAsync(response));
        await AssertNothingWasDecidedAsync();
    }

    #endregion

    #region Refusals both decisions share

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task An_unknown_business_is_not_found(string decision)
    {
        var response = await _factory.AsAdministrator().PostAsync($"{ListUrl}/999999/{decision}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNothingWasDecidedAsync();
    }

    /// <summary>
    /// Only a pending application may be decided: an already-trading business, an already-refused
    /// one and a deactivated one are each refused with the one sentence, and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("approve", BusinessStatus.Active)]
    [InlineData("approve", BusinessStatus.Rejected)]
    [InlineData("approve", BusinessStatus.Deactivated)]
    [InlineData("reject", BusinessStatus.Active)]
    [InlineData("reject", BusinessStatus.Rejected)]
    [InlineData("reject", BusinessStatus.Deactivated)]
    public async Task Only_a_pending_application_may_be_decided(string decision, BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.PendingBusinessId, status);

        var response = await _factory.AsAdministrator()
            .PostAsync($"{ListUrl}/{_factory.PendingBusinessId}/{decision}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BusinessDecisionPolicy.NotPendingMessage, await DetailAsync(response));
        Assert.Equal(status, (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
        Assert.Empty(_factory.LoggedEntries().Where(IsAuditEntry));
    }

    /// <summary>Deciding twice is refused the second time, and the first decision stands.</summary>
    [Fact]
    public async Task Deciding_an_already_decided_application_is_refused()
    {
        (await _factory.AsAdministrator().PostAsync(ApproveUrl(_factory.PendingBusinessId), null))
            .EnsureSuccessStatusCode();
        var decided = await _factory.BusinessAsync(_factory.PendingBusinessId);

        var again = await _factory.AsAdministrator().PostAsync(RejectUrl(_factory.PendingBusinessId), null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var unchanged = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Active, unchanged.Status);
        Assert.Equal(decided.DecidedAtUtc, unchanged.DecidedAtUtc);
        Assert.Null(unchanged.RejectionReason);
    }

    #endregion

    #region Helpers

    private const string ListUrl = "/api/super-admin/businesses";
    private const string PendingCountUrl = "/api/super-admin/businesses/pending-count";

    private static string ApproveUrl(int businessId) => $"{ListUrl}/{businessId}/approve";

    private static string RejectUrl(int businessId) => $"{ListUrl}/{businessId}/reject";

    /// <summary>
    /// Every endpoint on the surface, called once, so an authorization assertion is about the whole
    /// controller rather than whichever action a test happened to pick. The four lifecycle endpoints
    /// of issue #545 are here for exactly that reason: an endpoint that writes a business's status
    /// or releases somebody's membership must be refused for every caller who is not the configured
    /// administrator, and "every endpoint" has to keep meaning every endpoint.
    /// </summary>
    private async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(HttpClient client) =>
    [
        await client.GetAsync(ListUrl),
        await client.GetAsync(PendingCountUrl),
        await client.PostAsync(ApproveUrl(_factory.PendingBusinessId), null),
        await client.PostAsync(
            RejectUrl(_factory.PendingBusinessId),
            new StringContent("{\"reason\":\"no\"}", Encoding.UTF8, "application/json")),
        await client.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/deactivate", null),
        await client.PostAsync($"{ListUrl}/{_factory.TradingBusinessId}/reactivate", null),
        await client.GetAsync($"{ListUrl}/{_factory.TradingBusinessId}/members"),
        await client.PostAsync(
            $"{ListUrl}/{_factory.TradingBusinessId}/members/{_factory.TradingOwnerMembershipId}/release",
            null),
    ];

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
        entry.StartsWith("Platform business decision audit.", StringComparison.Ordinal);

    /// <summary>
    /// Any platform audit event at all - a decision's or a lifecycle action's (issue #545) - so
    /// "nothing happened" can be asserted about the whole surface rather than about one of the two
    /// trails.
    /// </summary>
    private static bool IsPlatformAuditEntry(string entry) =>
        IsAuditEntry(entry)
        || entry.StartsWith("Platform business lifecycle audit.", StringComparison.Ordinal);

    /// <summary>The one audit event the request under test emitted.</summary>
    private string AuditEntry() => Assert.Single(_factory.LoggedEntries().Where(IsAuditEntry));

    /// <summary>
    /// The pending application and the trading business are both untouched and nothing was audited -
    /// the assertion every refusal shares. The trading business is here because the lifecycle
    /// endpoints above are called against it: a refused caller must have changed neither.
    /// </summary>
    private async Task AssertNothingWasDecidedAsync()
    {
        var business = await _factory.BusinessAsync(_factory.PendingBusinessId);
        Assert.Equal(BusinessStatus.Pending, business.Status);
        Assert.Null(business.DecidedAtUtc);
        Assert.Null(business.RejectionReason);
        Assert.True((await _factory.MembershipAsync(_factory.ApplicantMembershipId)).IsActive);

        Assert.Equal(
            BusinessStatus.Active,
            (await _factory.BusinessAsync(_factory.TradingBusinessId)).Status);
        Assert.True((await _factory.MembershipAsync(_factory.TradingOwnerMembershipId)).IsActive);

        Assert.Empty(_factory.LoggedEntries().Where(IsPlatformAuditEntry));
    }

    #endregion
}

/// <summary>
/// The shipped state, as issue #336's own suite asserts for the diagnostics API: with no platform
/// administrator configured, nobody reaches this surface at all - not an Owner, and not the actor
/// who is the administrator on a host that configures one (issue #544).
/// </summary>
public class SuperAdminBusinessesUnconfiguredApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory.Unconfigured>, IAsyncLifetime
{
    private readonly SuperAdminBusinessesApiFactory.Unconfigured _factory;

    public SuperAdminBusinessesUnconfiguredApiTests(SuperAdminBusinessesApiFactory.Unconfigured factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.AdministratorObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    public async Task Nobody_reaches_the_surface_when_no_administrator_is_configured(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/super-admin/businesses")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/super-admin/businesses/{_factory.PendingBusinessId}/approve", null))
                .StatusCode);

        Assert.Equal(
            BusinessStatus.Pending,
            (await _factory.BusinessAsync(_factory.PendingBusinessId)).Status);
    }
}
