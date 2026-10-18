using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.ExternalIdentity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

// The two authorities' signing keys, issuers, audiences and metadata documents are issue #710's,
// reused rather than copied: this matrix has to exercise the validation that actually ships, and a
// second set of test keys would be a second definition of what "a customer identity token" is.
using Tokens = InventoryApi.Tests.Auth.TrustedIssuerTokenValidationTests.TestTokens;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The provider × account-state × role matrix of issue #714, run through the real request pipeline
/// with real tokens from **both** trusted issuers.
///
/// <para>Issue #710's <see cref="TrustedIssuerTokenValidationTests"/> proves what each bearer scheme
/// accepts and refuses. This class starts where that one stops: for a token both schemes have
/// already validated, it asserts that every membership decision after authentication - invitation
/// redemption, an application pending or rejected, a member's access, a business deactivated, a
/// membership revoked, the role's capabilities, the Super Admin boundary and two-business isolation
/// - comes out **identically whichever issuer signed the person in**, because none of those
/// decisions reads an issuer. That identity is the whole point of the Option C rollout (design
/// `docs/identity-provider-decision.md` § 3 and § 12): the second issuer adds a way to sign in and
/// changes nothing about what signing in gets you.</para>
///
/// <para><strong>What this cannot prove.</strong> These are synthetic tokens minted against stubbed
/// metadata documents. They prove the API's own rules; they prove nothing about whether a human has
/// provisioned an External ID tenant, enabled Google or email-and-password inside its user flow, or
/// associated the application with that flow - so a passing run here is not evidence that any hosted
/// journey works. That verification is a human staging step, and
/// <see href="../../docs/identity-rollout-verification.md">docs/identity-rollout-verification.md</see>
/// is the matrix that keeps the two kinds of evidence apart.</para>
/// </summary>
public sealed class CustomerIdentityRolloutMatrixTests : IClassFixture<CustomerIdentityRolloutMatrixTests.ApiFactory>
{
    /// <summary>
    /// Endpoints that must be refused to anybody whose membership does not resolve, whatever
    /// authority validated their token: the capability surface, the business record, a tenant-owned
    /// read and the member roster.
    /// </summary>
    private static readonly string[] BusinessEndpoints =
    [
        "/api/me/access",
        "/api/business/current",
        "/api/suppliers",
        "/api/members",
    ];

    private readonly ApiFactory _factory;

    public CustomerIdentityRolloutMatrixTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The journey that makes the second issuer useful at all (design § 14.1 phase 5): an Owner
    /// creates an invite and somebody who signed in through the *other* authority redeems it. It is
    /// asserted in both directions, because "every identity route" includes the workforce one: a
    /// customer identity Owner inviting a work account must work exactly as well.
    /// </summary>
    [Fact]
    public async Task An_invite_is_redeemable_through_either_issuer_and_grants_only_that_business()
    {
        await _factory.ResetAsync();

        var workforceInvite = await _factory.CreateInviteAsync(
            _factory.WorkforceOwner(), BusinessRole.Manager);
        var customerIdentityInvite = await _factory.CreateInviteAsync(
            _factory.CustomerIdentityOwner(), BusinessRole.Operator);

        // A Google or email/password account that belongs nowhere joins the workforce Owner's
        // business through the invite alone.
        var joiner = _factory.CustomerIdentity(ApiFactory.CustomerIdentityStrangerObjectId);
        var joined = await joiner.PostAsJsonAsync("/api/invites/redeem", new { token = workforceInvite });

        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(nameof(BusinessRole.Manager), (await Json(joined)).GetProperty("role").GetString());
        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Manager)),
            await AccessAsync(joiner));

        // And reads that business's data, and only that business's.
        Assert.Equal(new[] { ApiFactory.WorkforceSupplierName }, await SupplierNamesAsync(joiner));

        // The membership written is an ordinary one, recorded against the customer identity tenant.
        var membership = await _factory.MembershipAsync(
            Tokens.ExternalIdTenantId, ApiFactory.CustomerIdentityStrangerObjectId);
        Assert.Equal(ApiFactory.WorkforceBusinessId, membership.BusinessId);
        Assert.Equal(BusinessRole.Manager, membership.Role);
        Assert.True(membership.IsActive);

        // The other direction: a work account joins the business whose Owner signs in through the
        // customer identity tenant.
        var workforceJoiner = _factory.Workforce(ApiFactory.WorkforceStrangerObjectId);
        var workforceJoined = await workforceJoiner.PostAsJsonAsync(
            "/api/invites/redeem", new { token = customerIdentityInvite });

        Assert.Equal(HttpStatusCode.OK, workforceJoined.StatusCode);
        Assert.Equal(
            (ApiFactory.CustomerIdentityBusinessName, nameof(BusinessRole.Operator)),
            await AccessAsync(workforceJoiner));
        Assert.Equal(
            new[] { ApiFactory.CustomerIdentitySupplierName }, await SupplierNamesAsync(workforceJoiner));
    }

    /// <summary>
    /// The two supported hosted journeys, joining a business for real (design § 4 journeys C and D).
    /// A Google-brokered token carries an <c>idp</c> and the upstream Google <c>sub</c>; a local
    /// email/password token carries the tenant's own <c>email_verified</c> and no <c>idp</c> at all.
    /// Both become ordinary members with the invite's role, through the same redemption, and every one
    /// of those provider claims decides nothing: the same object id presented afterwards on a token
    /// carrying none of them resolves the same seat.
    /// </summary>
    [Fact]
    public async Task Each_hosted_journey_joins_a_business_the_same_way_and_its_provider_claims_decide_nothing()
    {
        await _factory.ResetAsync();

        var forGoogle = await _factory.CreateInviteAsync(_factory.WorkforceOwner(), BusinessRole.Manager);
        var forLocalAccount = await _factory.CreateInviteAsync(_factory.WorkforceOwner(), BusinessRole.Operator);

        var googleClaims = Tokens.MailboxClaims(
            Tokens.ExternalIdTenantId, ApiFactory.GoogleJourneyObjectId);
        googleClaims["idp"] = "google.com";
        googleClaims["sub"] = "108134655550123456789";

        var google = _factory.Bearer(Tokens.Token(
            Tokens.ExternalIdSigningKey,
            Tokens.ExternalIdIssuer,
            Tokens.ExternalIdApiClientId,
            googleClaims));

        var localAccount = _factory.Bearer(Tokens.LocalAccountAccessToken(
            ApiFactory.LocalAccountJourneyObjectId, "casey@example.test"));

        Assert.Equal(
            HttpStatusCode.OK,
            (await google.PostAsJsonAsync("/api/invites/redeem", new { token = forGoogle })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await localAccount.PostAsJsonAsync("/api/invites/redeem", new { token = forLocalAccount })).StatusCode);

        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Manager)),
            await AccessAsync(google));
        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Operator)),
            await AccessAsync(localAccount));

        // The identity is the directory object, so the same pair on a token carrying no `idp`, no
        // `sub` and no address at all is the same member.
        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Manager)),
            await AccessAsync(_factory.CustomerIdentity(ApiFactory.GoogleJourneyObjectId)));
        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Operator)),
            await AccessAsync(_factory.CustomerIdentity(ApiFactory.LocalAccountJourneyObjectId)));

        // One seat each, both recorded against the customer identity tenant.
        foreach (var objectId in new[] { ApiFactory.GoogleJourneyObjectId, ApiFactory.LocalAccountJourneyObjectId })
        {
            var membership = await _factory.MembershipAsync(Tokens.ExternalIdTenantId, objectId);
            Assert.True(membership.IsActive);
            Assert.Equal(ApiFactory.WorkforceBusinessId, membership.BusinessId);
        }
    }

    /// <summary>
    /// The conflict that stands in for a linking feature, which is deferred (#713, design § 12). One
    /// identity holds at most one active membership, so a member who signs in the other way and
    /// presents an invite is refused - they do not join a second business, and they certainly do not
    /// merge with the seat they already hold. The refusal is a <c>409</c> that names no business, the
    /// invite stays unused, and the membership they do have is untouched.
    /// </summary>
    [Fact]
    public async Task An_identity_that_already_belongs_somewhere_cannot_redeem_a_second_invite()
    {
        await _factory.ResetAsync();

        var workforceInvite = await _factory.CreateInviteAsync(
            _factory.WorkforceOwner(), BusinessRole.Manager);
        var customerIdentityInvite = await _factory.CreateInviteAsync(
            _factory.CustomerIdentityOwner(), BusinessRole.Manager);

        var customerIdentityMember = _factory.CustomerIdentity(ApiFactory.CustomerIdentityOperatorObjectId);
        var workforceMember = _factory.Workforce(ApiFactory.WorkforceOperatorObjectId);

        var refusedCustomerIdentity = await customerIdentityMember.PostAsJsonAsync(
            "/api/invites/redeem", new { token = workforceInvite });
        var refusedWorkforce = await workforceMember.PostAsJsonAsync(
            "/api/invites/redeem", new { token = customerIdentityInvite });

        Assert.Equal(HttpStatusCode.Conflict, refusedCustomerIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refusedWorkforce.StatusCode);

        foreach (var response in new[] { refusedCustomerIdentity, refusedWorkforce })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ApiFactory.WorkforceBusinessName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiFactory.CustomerIdentityBusinessName, body, StringComparison.Ordinal);
        }

        // Each of them still has exactly the seat they had, in the business they had it in.
        Assert.Equal(
            (ApiFactory.CustomerIdentityBusinessName, nameof(BusinessRole.Operator)),
            await AccessAsync(customerIdentityMember));
        Assert.Equal(
            (ApiFactory.WorkforceBusinessName, nameof(BusinessRole.Operator)),
            await AccessAsync(workforceMember));

        // And both invites are still waiting for somebody who may use them.
        Assert.Equal(
            new[] { MemberInviteStatus.Pending, MemberInviteStatus.Pending },
            await _factory.InviteStatusesAsync());
    }

    /// <summary>
    /// Every state an account can be in while it is not a usable member, reported identically for an
    /// identity from each issuer - and refused identically by every business endpoint. The
    /// account-state screen is the one thing any of them may read, and it names no business
    /// (issue #523).
    /// </summary>
    [Theory]
    [InlineData("NoMembership", null)]
    [InlineData("ApplicationPending", null)]
    [InlineData("ApplicationRejected", ApiFactory.RejectionReason)]
    [InlineData("BusinessDeactivated", null)]
    [InlineData("MembershipRevoked", null)]
    public async Task Every_non_member_account_state_is_reported_identically_through_both_issuers(
        string expectedState,
        string? expectedReason)
    {
        await _factory.ResetAsync();

        foreach (var client in new[]
        {
            _factory.CustomerIdentity(ApiFactory.CustomerIdentityObjectIdFor(expectedState)),
            _factory.Workforce(ApiFactory.WorkforceObjectIdFor(expectedState)),
        })
        {
            var accountState = await client.GetAsync("/api/me/account-state");
            var reported = await Json(accountState);

            Assert.Equal(HttpStatusCode.OK, accountState.StatusCode);
            Assert.Equal(expectedState, reported.GetProperty("state").GetString());
            Assert.Equal(expectedReason, reported.GetProperty("rejectionReason").GetString());

            foreach (var endpoint in BusinessEndpoints)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(endpoint)).StatusCode);
            }
        }
    }

    /// <summary>
    /// Revocation takes effect on the very next request, whichever authority issued the token - there
    /// is no per-issuer cache, session or grant that could outlive it. The row stays, which is what
    /// makes the state reportable and the reinstatement possible.
    /// </summary>
    [Fact]
    public async Task Revoking_a_membership_ends_access_on_the_next_request_through_either_issuer()
    {
        await _factory.ResetAsync();

        foreach (var (owner, member, objectId, directoryTenantId) in new[]
        {
            (_factory.CustomerIdentityOwner(),
                _factory.CustomerIdentity(ApiFactory.CustomerIdentityOperatorObjectId),
                ApiFactory.CustomerIdentityOperatorObjectId,
                Tokens.ExternalIdTenantId),
            (_factory.WorkforceOwner(),
                _factory.Workforce(ApiFactory.WorkforceOperatorObjectId),
                ApiFactory.WorkforceOperatorObjectId,
                Tokens.WorkforceTenantX),
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/suppliers")).StatusCode);

            var membershipId = (await _factory.MembershipAsync(directoryTenantId, objectId)).Id;
            var revoked = await owner.PostAsync($"/api/members/{membershipId}/revoke", content: null);
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/suppliers")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/me/access")).StatusCode);
            Assert.Equal("MembershipRevoked", await AccountStateAsync(member));

            // Reinstating gives the same seat back, with the role the row still held.
            Assert.Equal(
                HttpStatusCode.OK,
                (await owner.PostAsync($"/api/members/{membershipId}/reinstate", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/suppliers")).StatusCode);
        }
    }

    /// <summary>
    /// A business's lifecycle decides its members' access, and it does that without knowing how any
    /// of them signs in. Deactivation also cancels that business's live invites (issue #545), so a
    /// link created before it cannot be redeemed afterwards - and reactivation gives the members
    /// their own role back rather than a new one.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_ends_its_customer_identity_members_access_and_reactivation_restores_it()
    {
        await _factory.ResetAsync();

        var invite = await _factory.CreateInviteAsync(_factory.CustomerIdentityOwner(), BusinessRole.Manager);
        var administrator = _factory.Workforce(ApiFactory.AdministratorObjectId);
        var owner = _factory.CustomerIdentityOwner();

        var deactivated = await administrator.PostAsync(
            $"/api/super-admin/businesses/{ApiFactory.CustomerIdentityBusinessId}/deactivate", content: null);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        foreach (var endpoint in BusinessEndpoints)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(endpoint)).StatusCode);
        }

        Assert.Equal("BusinessDeactivated", await AccountStateAsync(owner));

        // The invite went with the business, so nobody joins a business nobody can reach.
        var stranger = _factory.CustomerIdentity(ApiFactory.CustomerIdentityStrangerObjectId);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await stranger.PostAsJsonAsync("/api/invites/redeem", new { token = invite })).StatusCode);

        var reactivated = await administrator.PostAsync(
            $"/api/super-admin/businesses/{ApiFactory.CustomerIdentityBusinessId}/reactivate", content: null);
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);

        Assert.Equal(
            (ApiFactory.CustomerIdentityBusinessName, nameof(BusinessRole.Owner)),
            await AccessAsync(owner));
    }

    /// <summary>
    /// A role is a role: the capabilities it grants, and the endpoints it reaches, are decided by
    /// <c>RoleCapabilities</c> and never by the issuer that validated the token. The two Operators
    /// below are told the same thing by <c>GET /api/me/access</c> and refused the same endpoints,
    /// and so are the two Owners.
    /// </summary>
    [Fact]
    public async Task A_role_grants_the_same_capabilities_whichever_issuer_signed_the_member_in()
    {
        await _factory.ResetAsync();

        var customerIdentityOperator = _factory.CustomerIdentity(ApiFactory.CustomerIdentityOperatorObjectId);
        var workforceOperator = _factory.Workforce(ApiFactory.WorkforceOperatorObjectId);

        Assert.Equal(
            await CapabilitiesAsync(workforceOperator),
            await CapabilitiesAsync(customerIdentityOperator));
        Assert.Equal(
            await CapabilitiesAsync(_factory.WorkforceOwner()),
            await CapabilitiesAsync(_factory.CustomerIdentityOwner()));

        foreach (var client in new[] { customerIdentityOperator, workforceOperator })
        {
            // Day-to-day work: yes. The Manager's expenses and the Owner's member roster: no.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/suppliers")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/operating-expenses")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/members")).StatusCode);
        }

        foreach (var client in new[] { _factory.CustomerIdentityOwner(), _factory.WorkforceOwner() })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/operating-expenses")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/members")).StatusCode);
        }
    }

    /// <summary>
    /// The administrator boundary across the whole platform-admin surface (design § 8 and § 11, T9),
    /// not just one endpoint of it: a customer identity reaches none of it, whether it holds an
    /// Owner's membership or none at all, and whether or not its object id is the administrator's own.
    /// The configured pair is a workforce identity and is compared as a whole pair, so sharing the
    /// customer tenant's <c>tid</c> with every self-registered stranger grants nothing.
    /// </summary>
    [Fact]
    public async Task A_customer_identity_reaches_no_platform_admin_endpoint()
    {
        await _factory.ResetAsync();

        string[] reads =
        [
            "/api/super-admin/businesses",
            "/api/super-admin/businesses/pending-count",
            $"/api/super-admin/businesses/{ApiFactory.CustomerIdentityBusinessId}/members",
            "/api/super-admin/support-sessions/active",
            "/api/admin/diagnostics/access",
        ];

        foreach (var client in new[]
        {
            _factory.CustomerIdentityOwner(),
            _factory.CustomerIdentity(ApiFactory.CustomerIdentityStrangerObjectId),
            // The administrator's own object id, presented from the customer identity tenant: a
            // different actor, because the pair is what is compared.
            _factory.CustomerIdentity(ApiFactory.AdministratorObjectId),
        })
        {
            foreach (var read in reads)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(read)).StatusCode);
            }

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsync(
                    $"/api/super-admin/businesses/{ApiFactory.CustomerIdentityBusinessId}/deactivate",
                    content: null)).StatusCode);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync(
                    $"/api/super-admin/support-sessions/{ApiFactory.CustomerIdentityBusinessId}",
                    new { reason = "Looking into a report" })).StatusCode);
        }

        // The configured workforce administrator is not refused, which is what makes the refusals
        // above a boundary rather than a broken surface.
        var administrator = _factory.Workforce(ApiFactory.AdministratorObjectId);
        foreach (var read in reads)
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, (await administrator.GetAsync(read)).StatusCode);
        }
    }

    /// <summary>
    /// Two-business isolation with the identities from both issuers (design § 11, T8). Each Owner
    /// reads their own business and nothing of the other's, and nothing a request carries can change
    /// that: a business id in a query string or a header is not how a business is resolved, and the
    /// support-session header is not a credential an ordinary member can profit from.
    /// </summary>
    [Fact]
    public async Task Two_businesses_whose_members_use_different_issuers_stay_isolated()
    {
        await _factory.ResetAsync();

        var workforceOwner = _factory.WorkforceOwner();
        var customerIdentityOwner = _factory.CustomerIdentityOwner();

        Assert.Equal(new[] { ApiFactory.WorkforceSupplierName }, await SupplierNamesAsync(workforceOwner));
        Assert.Equal(
            new[] { ApiFactory.CustomerIdentitySupplierName }, await SupplierNamesAsync(customerIdentityOwner));

        Assert.Equal(ApiFactory.WorkforceBusinessName, await CurrentBusinessNameAsync(workforceOwner));
        Assert.Equal(
            ApiFactory.CustomerIdentityBusinessName, await CurrentBusinessNameAsync(customerIdentityOwner));

        // A request that tries to name the other business is answered about its own.
        Assert.Equal(
            new[] { ApiFactory.CustomerIdentitySupplierName },
            await SupplierNamesAsync(
                customerIdentityOwner, $"/api/suppliers?businessId={ApiFactory.WorkforceBusinessId}"));

        // And a support-session header from somebody the platform-admin policy refuses is simply an
        // ordinary member's request (issue #551).
        var forged = _factory.CustomerIdentityOwner();
        forged.DefaultRequestHeaders.TryAddWithoutValidation("X-Support-Session", "not-a-session-id");
        Assert.Equal(new[] { ApiFactory.CustomerIdentitySupplierName }, await SupplierNamesAsync(forged));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(string? BusinessName, string? Role)> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/access");
        response.EnsureSuccessStatusCode();
        var access = await Json(response);

        return (access.GetProperty("businessName").GetString(), access.GetProperty("role").GetString());
    }

    private static async Task<string[]> CapabilitiesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/access");
        response.EnsureSuccessStatusCode();

        return (await Json(response))
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(capability => capability.GetString() ?? string.Empty)
            .ToArray();
    }

    private static async Task<string?> AccountStateAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/account-state");
        response.EnsureSuccessStatusCode();

        return (await Json(response)).GetProperty("state").GetString();
    }

    private static async Task<string?> CurrentBusinessNameAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/business/current");
        response.EnsureSuccessStatusCode();

        return (await Json(response)).GetProperty("name").GetString();
    }

    private static async Task<string[]> SupplierNamesAsync(
        HttpClient client, string path = "/api/suppliers")
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        return (await Json(response))
            .EnumerateArray()
            .Select(supplier => supplier.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>
    /// Hosts the real application with both bearer schemes configured, one configured platform
    /// administrator and one relational SQLite database, and replaces nothing but the two authorities'
    /// metadata documents. Every membership, business status, invite and supplier below is seeded
    /// from outside the request boundary; everything a test then asserts is the production pipeline's
    /// own answer.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string WorkforceBusinessName = "Workforce Vending";
        public const string CustomerIdentityBusinessName = "Customer Identity Vending";
        public const string WorkforceSupplierName = "Workforce Wholesale";
        public const string CustomerIdentitySupplierName = "Customer Identity Wholesale";
        public const string RejectionReason = "The ABN did not match the trading name.";

        public const int WorkforceBusinessId = 1;
        public const int CustomerIdentityBusinessId = 2;

        /// <summary>The configured platform administrator: a workforce identity, in no business.</summary>
        public const string AdministratorObjectId = "0a000000-0000-4000-8000-00000000000a";

        public const string WorkforceOwnerObjectId = "1b000000-0000-4000-8000-00000000001b";
        public const string WorkforceOperatorObjectId = "2b000000-0000-4000-8000-00000000002b";
        public const string WorkforceStrangerObjectId = "3b000000-0000-4000-8000-00000000003b";

        public const string CustomerIdentityOwnerObjectId = "1c000000-0000-4000-8000-00000000001c";
        public const string CustomerIdentityOperatorObjectId = "2c000000-0000-4000-8000-00000000002c";
        public const string CustomerIdentityStrangerObjectId = "3c000000-0000-4000-8000-00000000003c";

        /// <summary>The directory object a Google sign-up creates (design § 4 journey C).</summary>
        public const string GoogleJourneyObjectId = "8c000000-0000-4000-8000-00000000008c";

        /// <summary>The directory object a local email/password sign-up creates (journey D).</summary>
        public const string LocalAccountJourneyObjectId = "9c000000-0000-4000-8000-00000000009c";

        private const int PendingBusinessId = 3;
        private const int RejectedBusinessId = 4;
        private const int DeactivatedBusinessId = 5;

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        /// <summary>
        /// The identity in each non-member state, per issuer. One identity per state keeps the
        /// states independent of each other: the precedence in <c>AccountStatePolicy</c> is about one
        /// identity's own records, and a test that reused an identity would be asserting that
        /// precedence instead of the matrix.
        /// </summary>
        public static string CustomerIdentityObjectIdFor(string state) => state switch
        {
            "NoMembership" => CustomerIdentityStrangerObjectId,
            "ApplicationPending" => "4c000000-0000-4000-8000-00000000004c",
            "ApplicationRejected" => "5c000000-0000-4000-8000-00000000005c",
            "BusinessDeactivated" => "6c000000-0000-4000-8000-00000000006c",
            "MembershipRevoked" => "7c000000-0000-4000-8000-00000000007c",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No identity is seeded in that state."),
        };

        public static string WorkforceObjectIdFor(string state) => state switch
        {
            "NoMembership" => WorkforceStrangerObjectId,
            "ApplicationPending" => "4b000000-0000-4000-8000-00000000004b",
            "ApplicationRejected" => "5b000000-0000-4000-8000-00000000005b",
            "BusinessDeactivated" => "6b000000-0000-4000-8000-00000000006b",
            "MembershipRevoked" => "7b000000-0000-4000-8000-00000000007b",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No identity is seeded in that state."),
        };

        /// <summary>A client carrying a real access token the customer identity tenant issued.</summary>
        public HttpClient CustomerIdentity(string objectId) =>
            Bearer(Tokens.ExternalIdAccessToken(objectId));

        /// <summary>A client carrying a real access token a workforce Entra tenant issued.</summary>
        public HttpClient Workforce(string objectId) =>
            Bearer(Tokens.WorkforceAccessToken(Tokens.WorkforceTenantX, objectId));

        public HttpClient WorkforceOwner() => Workforce(WorkforceOwnerObjectId);

        public HttpClient CustomerIdentityOwner() => CustomerIdentity(CustomerIdentityOwnerObjectId);

        /// <summary>
        /// Creates an invite through the Owner's own endpoint and returns the token it reported once,
        /// so a redemption in these tests presents what a person opening the link presents.
        /// </summary>
        public async Task<string> CreateInviteAsync(HttpClient owner, BusinessRole role)
        {
            var response = await owner.PostAsJsonAsync(
                "/api/members/invites", new { role = role.ToString(), note = "Issue #714 verification" });
            response.EnsureSuccessStatusCode();

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement
                .GetProperty("token")
                .GetString()!;
        }

        public async Task<BusinessMembership> MembershipAsync(string directoryTenantId, string objectId)
        {
            await using var db = Database();

            return await db.BusinessMemberships
                .AsNoTracking()
                .SingleAsync(membership =>
                    membership.DirectoryTenantId == directoryTenantId && membership.ObjectId == objectId);
        }

        /// <summary>Every stored invite's status, in key order.</summary>
        public async Task<MemberInviteStatus[]> InviteStatusesAsync()
        {
            await using var db = Database();

            return await db.MemberInvites
                .AsNoTracking()
                .OrderBy(invite => invite.Id)
                .Select(invite => invite.Status)
                .ToArrayAsync();
        }

        /// <summary>
        /// Puts the world back exactly as seeded. These tests revoke memberships, deactivate
        /// businesses and redeem invites, so each one starts from the same five businesses rather
        /// than from whatever its predecessor left behind.
        /// </summary>
        public async Task ResetAsync()
        {
            await using var db = Database();

            db.SupportSessions.RemoveRange(await db.SupportSessions.ToListAsync());
            db.MemberInvites.RemoveRange(await db.MemberInvites.ToListAsync());
            await db.SaveChangesAsync();

            db.Suppliers.RemoveRange(await db.Suppliers.ToListAsync());
            db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
            await db.SaveChangesAsync();

            db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
            await db.SaveChangesAsync();

            db.Businesses.AddRange(
                Business(WorkforceBusinessId, WorkforceBusinessName, BusinessStatus.Active),
                Business(CustomerIdentityBusinessId, CustomerIdentityBusinessName, BusinessStatus.Active),
                Business(PendingBusinessId, "Pending Vending", BusinessStatus.Pending),
                Rejected(RejectedBusinessId, "Rejected Vending"),
                Business(DeactivatedBusinessId, "Deactivated Vending", BusinessStatus.Deactivated));
            await db.SaveChangesAsync();

            db.Suppliers.AddRange(
                Supplier(WorkforceBusinessId, WorkforceSupplierName),
                Supplier(CustomerIdentityBusinessId, CustomerIdentitySupplierName));

            db.BusinessMemberships.AddRange(
                Membership(WorkforceBusinessId, Tokens.WorkforceTenantX, WorkforceOwnerObjectId, BusinessRole.Owner),
                Membership(
                    WorkforceBusinessId, Tokens.WorkforceTenantX, WorkforceOperatorObjectId, BusinessRole.Operator),
                Membership(
                    CustomerIdentityBusinessId,
                    Tokens.ExternalIdTenantId,
                    CustomerIdentityOwnerObjectId,
                    BusinessRole.Owner),
                Membership(
                    CustomerIdentityBusinessId,
                    Tokens.ExternalIdTenantId,
                    CustomerIdentityOperatorObjectId,
                    BusinessRole.Operator));

            foreach (var directoryTenantId in new[] { Tokens.WorkforceTenantX, Tokens.ExternalIdTenantId })
            {
                Func<string, string> objectIdFor = directoryTenantId == Tokens.WorkforceTenantX
                    ? WorkforceObjectIdFor
                    : CustomerIdentityObjectIdFor;

                db.BusinessMemberships.AddRange(
                    // An applicant waiting for a decision: an active membership in a Pending business.
                    Membership(
                        PendingBusinessId, directoryTenantId, objectIdFor("ApplicationPending"), BusinessRole.Owner),
                    // A refused applicant: the rejection released their membership.
                    Inactive(Membership(
                        RejectedBusinessId, directoryTenantId, objectIdFor("ApplicationRejected"), BusinessRole.Owner)),
                    // A member of a business that was switched off under them.
                    Membership(
                        DeactivatedBusinessId, directoryTenantId, objectIdFor("BusinessDeactivated"),
                        BusinessRole.Manager),
                    // Somebody an Owner revoked.
                    Inactive(Membership(
                        WorkforceBusinessId, directoryTenantId, objectIdFor("MembershipRevoked"),
                        BusinessRole.Operator)));
            }

            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "common");
            builder.UseSetting("AzureAd:ClientId", Tokens.WorkforceApiClientId);
            builder.UseSetting("AzureAd:Scopes", "access_as_user");
            builder.UseSetting("ExternalId:Instance", Tokens.ExternalIdInstance);
            builder.UseSetting("ExternalId:TenantId", Tokens.ExternalIdTenantId);
            builder.UseSetting("ExternalId:ClientId", Tokens.ExternalIdApiClientId);
            builder.UseSetting("ExternalId:Scopes", "access_as_user");

            builder.UseSetting("PlatformAdmin:DirectoryTenantId", Tokens.WorkforceTenantX);
            builder.UseSetting("PlatformAdmin:ObjectId", AdministratorObjectId);

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            // The only thing replaced about authentication: where each scheme reads its issuer and
            // signing keys from. Every validation rule, claim and scheme is the production one.
            builder.ConfigureTestServices(services =>
            {
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(Tokens.WorkforceMetadata));

                services.Configure<JwtBearerOptions>(
                    ExternalIdBearerScheme.Name,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(Tokens.ExternalIdMetadata));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
        private AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which applies the migrations this
            // context then reads and writes through.
            _ = Services;

            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        /// <summary>
        /// A client carrying whatever access token a test minted, for the journey-flavoured tokens
        /// the two hosted journeys produce.
        /// </summary>
        public HttpClient Bearer(string accessToken)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }

        private static Business Business(int id, string name, BusinessStatus status) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = status,
                CreatedAtUtc = CreatedAt,
            };

        private static Business Rejected(int id, string name)
        {
            var business = Business(id, name, BusinessStatus.Rejected);
            business.DecidedAtUtc = CreatedAt;
            business.RejectionReason = RejectionReason;
            return business;
        }

        private static Supplier Supplier(int businessId, string name) =>
            new() { BusinessId = businessId, Name = name };

        private static BusinessMembership Membership(
            int businessId, string directoryTenantId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = directoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        private static BusinessMembership Inactive(BusinessMembership membership)
        {
            membership.IsActive = false;
            return membership;
        }
    }
}
