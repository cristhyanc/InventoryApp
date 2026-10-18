using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Real JWT bearer validation across the two trusted authorities (issue #710, the complete § 5.4
/// regression table of docs/identity-provider-decision.md).
///
/// <para>
/// These tests mint tokens with their own signing keys and hand each scheme a stubbed OpenID
/// Connect metadata document, so the validation under test is the production one - signature,
/// issuer, audience, tenant binding, lifetime, token purpose and the delegated scope - rather than
/// the end-to-end host's synthetic bypass. No test key or metadata stub is reachable from any
/// non-test code path: both are supplied here, to this host, after the application has composed
/// itself.
/// </para>
///
/// <para>
/// The asymmetry between the two schemes is the thing most likely to be broken by a later edit in
/// a way nothing notices, so it is asserted from both sides. The workforce scheme stays
/// multi-tenant: tokens from two <em>different</em> Entra tenants are both accepted and resolve to
/// their own actor, because a scheme accidentally pinned to one tenant would still pass a
/// one-tenant test while having broken sign-in for every other tenant's users. The customer
/// identity scheme accepts one pinned issuer, one audience and one tenant, and nothing else.
/// </para>
/// </summary>
public sealed class TrustedIssuerTokenValidationTests : IClassFixture<TrustedIssuerTokenValidationTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public TrustedIssuerTokenValidationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_workforce_token_from_either_tenant_is_accepted_and_resolves_its_own_actor()
    {
        await _factory.SeedAsync();

        var fromTenantX = await Access(TestTokens.WorkforceAccessToken(
            TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId));
        var fromTenantY = await Access(TestTokens.WorkforceAccessToken(
            TestTokens.WorkforceTenantY, TestTokens.TenantYManagerObjectId));

        Assert.Equal(HttpStatusCode.OK, fromTenantX.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fromTenantY.StatusCode);
        Assert.Equal(
            (ApiFactory.TenantXBusinessName, nameof(BusinessRole.Owner)),
            await ResolvedBusinessAsync(fromTenantX));
        Assert.Equal(
            (ApiFactory.TenantYBusinessName, nameof(BusinessRole.Manager)),
            await ResolvedBusinessAsync(fromTenantY));
    }

    [Fact]
    public async Task A_customer_identity_token_from_the_configured_tenant_is_accepted()
    {
        await _factory.SeedAsync();

        var response = await Access(TestTokens.ExternalIdAccessToken(TestTokens.ExternalIdMemberObjectId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            (ApiFactory.ExternalIdBusinessName, nameof(BusinessRole.Owner)),
            await ResolvedBusinessAsync(response));
    }

    [Fact]
    public async Task Both_pinned_spellings_of_a_scheme_own_audience_are_accepted()
    {
        await _factory.SeedAsync();

        var workforce = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            $"api://{TestTokens.WorkforceApiClientId}",
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        var customerIdentity = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            $"api://{TestTokens.ExternalIdApiClientId}",
            TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

        Assert.Equal(HttpStatusCode.OK, workforce.StatusCode);
        Assert.Equal(HttpStatusCode.OK, customerIdentity.StatusCode);
    }

    /// <summary>
    /// The same object id in two trusted tenants is two actors, not one. Entra signs every
    /// tenant's tokens with the same workforce key set, so nothing but the <c>(tid, oid)</c> pair
    /// keeps these two apart - and each resolves its own business and its own role.
    /// </summary>
    [Fact]
    public async Task One_object_id_under_two_trusted_tenants_resolves_two_separate_actors()
    {
        await _factory.SeedAsync();

        var workforce = await Access(TestTokens.WorkforceAccessToken(
            TestTokens.WorkforceTenantY, TestTokens.SharedObjectId));
        var customerIdentity = await Access(TestTokens.ExternalIdAccessToken(TestTokens.SharedObjectId));

        Assert.Equal(
            (ApiFactory.TenantYBusinessName, nameof(BusinessRole.Operator)),
            await ResolvedBusinessAsync(workforce));
        Assert.Equal(
            (ApiFactory.ExternalIdBusinessName, nameof(BusinessRole.Manager)),
            await ResolvedBusinessAsync(customerIdentity));
    }

    /// <summary>
    /// The highest-severity threat of the whole change (design § 11, T1). An attacker creates
    /// their own Entra tenant in minutes, and Microsoft signs its tokens with the same workforce
    /// key set as everybody else's - so the signature is genuine. What stops it is the
    /// issuer-to-<c>tid</c> binding: a token issued by the attacker's tenant can only ever assert
    /// the attacker's own tenant, whichever audience it asks for.
    /// </summary>
    [Fact]
    public async Task A_token_signed_for_an_attacker_tenant_cannot_claim_the_customer_identity_tenant()
    {
        await _factory.SeedAsync();

        foreach (var audience in new[] { TestTokens.WorkforceApiClientId, TestTokens.ExternalIdApiClientId })
        {
            var forged = await Access(TestTokens.Token(
                TestTokens.WorkforceSigningKey,
                TestTokens.WorkforceIssuer(TestTokens.AttackerTenantId),
                audience,
                TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }
    }

    [Fact]
    public async Task The_customer_identity_issuer_with_a_foreign_tenant_claim_is_refused()
    {
        await _factory.SeedAsync();

        var response = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Adding the second scheme must not loosen the binding the workforce scheme has always
    /// applied: one Entra tenant's issuer cannot carry another tenant's <c>tid</c>.
    /// </summary>
    [Fact]
    public async Task A_workforce_token_whose_tenant_claim_disagrees_with_its_issuer_is_refused()
    {
        await _factory.SeedAsync();

        var response = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            TestTokens.WorkforceApiClientId,
            TestTokens.Claims(TestTokens.WorkforceTenantY, TestTokens.TenantYManagerObjectId)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An issuer neither rule accepts matches no scheme's accepted values, including a second
    /// customer identity tenant - the configured one is a single pinned value, not a pattern that
    /// any <c>ciamlogin.com</c> tenant satisfies.
    /// </summary>
    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("https://login.example.com/common/v2.0")]
    [InlineData("https://dddddddd-0000-4000-8000-00000000000d.ciamlogin.com/dddddddd-0000-4000-8000-00000000000d/v2.0")]
    [InlineData("not-an-absolute-uri")]
    [InlineData(null)]
    public async Task An_issuer_neither_scheme_accepts_is_refused(string? issuer)
    {
        await _factory.SeedAsync();

        foreach (var (key, audience) in new[]
        {
            (TestTokens.WorkforceSigningKey, TestTokens.WorkforceApiClientId),
            (TestTokens.ExternalIdSigningKey, TestTokens.ExternalIdApiClientId),
        })
        {
            var response = await Access(TestTokens.Token(
                key,
                issuer,
                audience,
                TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// Each scheme validates only its own registration's audience, and the two are never pooled:
    /// a token one tenant issued for its own API is not a token for the other's (design § 11, T2).
    /// </summary>
    [Fact]
    public async Task Each_scheme_validates_only_its_own_audience()
    {
        await _factory.SeedAsync();

        var customerAudienceThroughWorkforce = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            TestTokens.ExternalIdApiClientId,
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        var workforceAudienceThroughCustomerIdentity = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.WorkforceApiClientId,
            TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

        Assert.Equal(HttpStatusCode.Unauthorized, customerAudienceThroughWorkforce.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, workforceAudienceThroughCustomerIdentity.StatusCode);
    }

    /// <summary>
    /// A token legitimately issued for another application - the SPA itself, or Microsoft Graph -
    /// is not a token for this API, from either authority.
    /// </summary>
    [Theory]
    [InlineData(TestTokens.SpaClientId)]
    [InlineData("https://graph.microsoft.com")]
    public async Task A_token_for_another_application_is_refused_by_both_schemes(string audience)
    {
        await _factory.SeedAsync();

        var workforce = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            audience,
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        var customerIdentity = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            audience,
            TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

        Assert.Equal(HttpStatusCode.Unauthorized, workforce.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, customerIdentity.StatusCode);
    }

    /// <summary>
    /// An ID token names the right person and is signed by a trusted authority, which is exactly
    /// what makes substituting it for an access token attractive (design § 11, T3). It carries no
    /// delegated scope and carries the claims that bind it to a sign-in, and it is refused even
    /// when its audience is this API's own registration.
    /// </summary>
    [Fact]
    public async Task An_id_token_is_not_an_access_token_for_either_authority()
    {
        await _factory.SeedAsync();

        foreach (var (key, issuer, audience, tenantId, objectId) in new[]
        {
            (TestTokens.WorkforceSigningKey, TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
                TestTokens.WorkforceApiClientId, TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId),
            (TestTokens.ExternalIdSigningKey, TestTokens.ExternalIdIssuer,
                TestTokens.ExternalIdApiClientId, TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId),
        })
        {
            var idToken = TestTokens.Token(key, issuer, audience, new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = tenantId,
                ["oid"] = objectId,
                ["nonce"] = "0a9f3c7e2b5d41f8",
            });

            Assert.Equal(HttpStatusCode.Unauthorized, (await Access(idToken)).StatusCode);
        }
    }

    /// <summary>
    /// An app-only token carries application roles and no delegated scope. This API has no
    /// app-only surface, so there is no branch to add for one: it is refused at authentication.
    /// </summary>
    [Fact]
    public async Task An_app_only_token_is_refused_by_both_schemes()
    {
        await _factory.SeedAsync();

        foreach (var (key, issuer, audience, tenantId, objectId) in new[]
        {
            (TestTokens.WorkforceSigningKey, TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
                TestTokens.WorkforceApiClientId, TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId),
            (TestTokens.ExternalIdSigningKey, TestTokens.ExternalIdIssuer,
                TestTokens.ExternalIdApiClientId, TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId),
        })
        {
            var appOnly = TestTokens.Token(key, issuer, audience, new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = tenantId,
                ["oid"] = objectId,
                ["roles"] = new[] { "Inventory.ReadAll" },
            });

            Assert.Equal(HttpStatusCode.Unauthorized, (await Access(appOnly)).StatusCode);
        }
    }

    /// <summary>
    /// A key one authority publishes must never validate a token claiming the other's issuer, and
    /// a key neither publishes must validate nothing at all.
    /// </summary>
    [Fact]
    public async Task A_token_signed_with_a_key_the_authority_does_not_publish_is_refused()
    {
        await _factory.SeedAsync();

        var untrustedWorkforce = await Access(TestTokens.Token(
            TestTokens.UntrustedSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            TestTokens.WorkforceApiClientId,
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        var untrustedCustomerIdentity = await Access(TestTokens.Token(
            TestTokens.UntrustedSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

        // The other authority's key, which is a real key published by a real trusted authority -
        // just not this one's.
        var customerIdentityTokenSignedByWorkforce = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            TestTokens.Claims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId)));

        Assert.Equal(HttpStatusCode.Unauthorized, untrustedWorkforce.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, untrustedCustomerIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, customerIdentityTokenSignedByWorkforce.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_not_yet_valid_token_is_refused_by_both_schemes()
    {
        await _factory.SeedAsync();

        var now = DateTime.UtcNow;

        foreach (var (key, issuer, audience, tenantId, objectId) in new[]
        {
            (TestTokens.WorkforceSigningKey, TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
                TestTokens.WorkforceApiClientId, TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId),
            (TestTokens.ExternalIdSigningKey, TestTokens.ExternalIdIssuer,
                TestTokens.ExternalIdApiClientId, TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId),
        })
        {
            var claims = TestTokens.Claims(tenantId, objectId);

            var expired = await Access(TestTokens.Token(
                key, issuer, audience, claims,
                notBefore: now.AddHours(-2), expires: now.AddHours(-1)));

            var notYetValid = await Access(TestTokens.Token(
                key, issuer, audience, claims,
                notBefore: now.AddHours(1), expires: now.AddHours(2)));

            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, notYetValid.StatusCode);
        }
    }

    /// <summary>
    /// The <c>401</c>/<c>403</c> separation is unchanged for the tokens this API has always
    /// accepted: a delegated access token that simply does not carry <c>access_as_user</c> is
    /// authenticated and then refused by <c>[RequiredScope]</c>, from either authority.
    /// </summary>
    [Fact]
    public async Task A_delegated_token_without_the_required_scope_is_forbidden_not_unauthorized()
    {
        await _factory.SeedAsync();

        foreach (var (key, issuer, audience, tenantId, objectId) in new[]
        {
            (TestTokens.WorkforceSigningKey, TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
                TestTokens.WorkforceApiClientId, TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId),
            (TestTokens.ExternalIdSigningKey, TestTokens.ExternalIdIssuer,
                TestTokens.ExternalIdApiClientId, TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId),
        })
        {
            var wrongScope = TestTokens.Token(key, issuer, audience, new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = tenantId,
                ["oid"] = objectId,
                ["scp"] = "User.Read",
            });

            Assert.Equal(HttpStatusCode.Forbidden, (await Access(wrongScope)).StatusCode);
        }
    }

    /// <summary>
    /// External ID issues <c>oid</c> and <c>tid</c> only when the token request asks for the
    /// <c>profile</c> scope (design § 4). A token that was requested without it cannot be
    /// attributed to anybody: the half that is missing decides where it is refused, and neither
    /// outcome resolves an actor.
    /// </summary>
    [Fact]
    public async Task A_customer_identity_token_without_the_profile_claims_resolves_no_actor()
    {
        await _factory.SeedAsync();

        var withoutObjectId = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = TestTokens.ExternalIdTenantId,
                ["scp"] = "access_as_user",
            }));

        var withoutEitherClaim = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["scp"] = "access_as_user" }));

        // Authenticated, but nobody: no actor, so no business, so no business data.
        Assert.Equal(HttpStatusCode.Forbidden, withoutObjectId.StatusCode);

        // Not even authenticated: the scheme is pinned to one tenant, and a token that claims none
        // is not from it.
        Assert.Equal(HttpStatusCode.Unauthorized, withoutEitherClaim.StatusCode);
    }

    /// <summary>
    /// The identity accessor's rejections are not weakened by a second issuer: a conflicting or
    /// non-GUID identity claim resolves nobody, whichever authority validated the token.
    /// </summary>
    [Fact]
    public async Task Conflicting_or_malformed_identity_claims_resolve_no_actor()
    {
        await _factory.SeedAsync();

        var conflictingObjectIds = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            TestTokens.WorkforceApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = TestTokens.WorkforceTenantX,
                ["oid"] = new[] { TestTokens.TenantXOwnerObjectId, TestTokens.SharedObjectId },
                ["scp"] = "access_as_user",
            }));

        var nonGuidObjectId = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = TestTokens.ExternalIdTenantId,
                ["oid"] = "customer@example.com",
                ["scp"] = "access_as_user",
            }));

        var conflictingTenants = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = new[] { TestTokens.ExternalIdTenantId, TestTokens.WorkforceTenantX },
                ["oid"] = TestTokens.ExternalIdMemberObjectId,
                ["scp"] = "access_as_user",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, conflictingObjectIds.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonGuidObjectId.StatusCode);

        // The pinned scheme refuses a disagreement about its own tenant before the actor is read.
        Assert.Equal(HttpStatusCode.Unauthorized, conflictingTenants.StatusCode);
    }

    /// <summary>
    /// The success criterion of the design's phase 3: a customer identity that belongs to no
    /// business authenticates and then reads nothing, exactly as an authenticated workforce
    /// stranger does. The one endpoint it may still reach is the account-state screen, which names
    /// no business.
    /// </summary>
    [Fact]
    public async Task A_customer_identity_with_no_membership_authenticates_and_reads_nothing()
    {
        await _factory.SeedAsync();

        var token = TestTokens.ExternalIdAccessToken(TestTokens.ExternalIdStrangerObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/me/access", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/products", token)).StatusCode);

        var accountState = await Get("/api/me/account-state", token);
        using var document = JsonDocument.Parse(await accountState.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, accountState.StatusCode);
        Assert.Equal("NoMembership", document.RootElement.GetProperty("state").GetString());
    }

    /// <summary>
    /// Issue #711's central guarantee, and the one the SPA's new Google action makes reachable: a
    /// mailbox is not an identity. The same person's Google account, brokered by the customer
    /// identity tenant and carrying the <em>same verified email address</em> as an existing
    /// workforce member, is a new actor with no membership, no role and no inherited access - and
    /// nothing is written to make it one, because identity linking is deferred (design § 12, and
    /// § 10.1: no authorization, membership, invite or onboarding decision reads an address).
    /// </summary>
    [Fact]
    public async Task A_google_identity_that_shares_a_members_mailbox_inherits_nothing_from_them()
    {
        await _factory.SeedAsync();

        var member = await Access(TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.WorkforceTenantX),
            TestTokens.WorkforceApiClientId,
            TestTokens.MailboxClaims(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId)));

        // The same mailbox, verified by Google, on a directory object this application has never
        // seen - which is exactly what a member signing in a second way produces.
        var google = TestTokens.MailboxClaims(TestTokens.ExternalIdTenantId, TestTokens.GoogleFederatedObjectId);
        google["idp"] = "google.com";
        google["sub"] = TestTokens.GoogleSubject;

        var googleToken = TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            google);

        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/me/access", googleToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/products", googleToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/business/current", googleToken)).StatusCode);

        // The one screen they may reach is the no-membership journey, which names no business.
        var accountState = await Get("/api/me/account-state", googleToken);
        using var document = JsonDocument.Parse(await accountState.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, accountState.StatusCode);
        Assert.Equal("NoMembership", document.RootElement.GetProperty("state").GetString());

        // Nothing was linked: presenting that token creates no membership of any kind.
        Assert.Equal(
            0,
            await _factory.MembershipCountAsync(TestTokens.ExternalIdTenantId, TestTokens.GoogleFederatedObjectId));

        // And the member whose address it is keeps their own business and role, untouched.
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(
            (ApiFactory.TenantXBusinessName, nameof(BusinessRole.Owner)),
            await ResolvedBusinessAsync(member));
    }

    /// <summary>
    /// The identity is the one the broker issued, never the upstream provider's subject
    /// (design § 4, journey C, and § 7's explicit prohibitions). A Google-federated token resolves
    /// through its External ID <c>oid</c> while the Google <c>sub</c> and <c>idp</c> beside it are
    /// read by nothing - and a token carrying only that upstream subject is nobody at all, however
    /// exactly the subject matches a stored object id.
    /// </summary>
    [Fact]
    public async Task A_customer_identity_is_its_directory_object_id_never_the_upstream_google_subject()
    {
        await _factory.SeedAsync();

        var brokered = TestTokens.MailboxClaims(TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId);
        brokered["idp"] = "google.com";
        brokered["sub"] = TestTokens.GoogleSubject;

        var resolved = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            brokered));

        var subjectInsteadOfObjectId = await Access(TestTokens.Token(
            TestTokens.ExternalIdSigningKey,
            TestTokens.ExternalIdIssuer,
            TestTokens.ExternalIdApiClientId,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tid"] = TestTokens.ExternalIdTenantId,
                ["sub"] = TestTokens.ExternalIdMemberObjectId,
                ["idp"] = "google.com",
                ["scp"] = "access_as_user",
            }));

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(
            (ApiFactory.ExternalIdBusinessName, nameof(BusinessRole.Owner)),
            await ResolvedBusinessAsync(resolved));

        // Authenticated, but nobody: `sub` is read by nothing, so there is no actor to resolve.
        Assert.Equal(HttpStatusCode.Forbidden, subjectInsteadOfObjectId.StatusCode);
    }

    /// <summary>
    /// The platform administrator is one configured <c>(tid, oid)</c> pair, compared as a whole
    /// pair. A second trusted issuer does not create a second way to be it: the same object id
    /// under the customer identity tenant is a different actor, and a token forged to claim the
    /// administrator's tenant never validates at all.
    /// </summary>
    [Fact]
    public async Task A_second_trusted_issuer_is_no_route_to_the_platform_administrator()
    {
        await _factory.SeedAsync();

        const string PendingCount = "/api/super-admin/businesses/pending-count";

        var administrator = await Get(PendingCount, TestTokens.WorkforceAccessToken(
            TestTokens.WorkforceTenantX, TestTokens.PlatformAdminObjectId));

        var sameObjectIdFromTheCustomerTenant = await Get(
            PendingCount, TestTokens.ExternalIdAccessToken(TestTokens.PlatformAdminObjectId));

        var forgedAdministratorTenant = await Get(PendingCount, TestTokens.Token(
            TestTokens.WorkforceSigningKey,
            TestTokens.WorkforceIssuer(TestTokens.AttackerTenantId),
            TestTokens.WorkforceApiClientId,
            TestTokens.Claims(TestTokens.WorkforceTenantX, TestTokens.PlatformAdminObjectId)));

        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sameObjectIdFromTheCustomerTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedAdministratorTenant.StatusCode);
    }

    /// <summary>
    /// No token, and an authorization header that is not a readable token, both end where they
    /// always did: a <c>401</c> with the bearer challenge. The policy scheme in front of the two
    /// validators has no token to route on and must not change that answer.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not-a-json-web-token")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==")]
    public async Task A_request_without_a_readable_bearer_token_is_challenged(string? authorizationHeader)
    {
        await _factory.SeedAsync();

        var client = _factory.CreateClient();
        if (authorizationHeader is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorizationHeader);
        }

        var response = await client.GetAsync("/api/me/access");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
    }

    /// <summary>
    /// An outage of the customer identity authority must not affect workforce sign-in, and must
    /// not cause this API to accept anything: a token it cannot validate is <c>401</c> (design
    /// § 9.3 and § 11, T10). Validation never degrades to skipping the issuer check or trusting an
    /// unverified signature, and an unreachable authority is exactly this - metadata that cannot
    /// be read.
    /// </summary>
    [Fact]
    public async Task An_unavailable_customer_identity_authority_refuses_its_tokens_and_leaves_workforce_signin_working()
    {
        using var factory = new ApiFactory(new UnavailableMetadata());
        await factory.SeedAsync();

        var customerIdentity = await Get(
            factory, "/api/me/access", TestTokens.ExternalIdAccessToken(TestTokens.ExternalIdMemberObjectId));
        var workforce = await Get(
            factory,
            "/api/me/access",
            TestTokens.WorkforceAccessToken(TestTokens.WorkforceTenantX, TestTokens.TenantXOwnerObjectId));

        Assert.Equal(HttpStatusCode.Unauthorized, customerIdentity.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workforce.StatusCode);
    }

    /// <summary>
    /// A key set that no longer publishes the key a token was signed with - a rotation this API
    /// could not refresh through to - fails closed rather than accepting the token on the strength
    /// of its issuer and audience alone.
    /// </summary>
    [Fact]
    public async Task A_key_set_without_the_tokens_signing_key_refuses_the_token()
    {
        var rotated = new OpenIdConnectConfiguration { Issuer = TestTokens.ExternalIdIssuer };
        rotated.SigningKeys.Add(TestTokens.UntrustedSigningKey);

        using var factory = new ApiFactory(new StaticConfigurationManager<OpenIdConnectConfiguration>(rotated));
        await factory.SeedAsync();

        var response = await Get(
            factory, "/api/me/access", TestTokens.ExternalIdAccessToken(TestTokens.ExternalIdMemberObjectId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private Task<HttpResponseMessage> Access(string bearerToken) => Get(_factory, "/api/me/access", bearerToken);

    private Task<HttpResponseMessage> Get(string path, string bearerToken) => Get(_factory, path, bearerToken);

    private static async Task<HttpResponseMessage> Get(
        WebApplicationFactory<Program> factory, string path, string bearerToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.GetAsync(path);
    }

    /// <summary>
    /// What an unreachable authority, or one whose key refresh fails, looks like to the bearer
    /// handler: no metadata document at all.
    /// </summary>
    private sealed class UnavailableMetadata : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            throw new InvalidOperationException("The identity authority's metadata document is unavailable.");

        public void RequestRefresh()
        {
        }
    }

    private static async Task<(string? BusinessName, string? Role)> ResolvedBusinessAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("businessName").GetString(),
            document.RootElement.GetProperty("role").GetString());
    }

    /// <summary>
    /// The two authorities' keys, metadata documents and tokens. The signing keys stand in for the
    /// key sets the real authorities publish, including the fact that Entra signs every tenant's
    /// tokens with one shared workforce key set - which is why the issuer-to-<c>tid</c> binding,
    /// not the key, is what stops a token minted for an attacker's own tenant from claiming
    /// another tenant's identity.
    /// </summary>
    internal static class TestTokens
    {
        public const string WorkforceApiClientId = "1a2b3c4d-0000-4000-8000-000000000001";
        public const string ExternalIdApiClientId = "1a2b3c4d-0000-4000-8000-000000000002";
        public const string SpaClientId = "1a2b3c4d-0000-4000-8000-000000000003";

        public const string WorkforceTenantX = "aaaaaaaa-0000-4000-8000-00000000000a";
        public const string WorkforceTenantY = "bbbbbbbb-0000-4000-8000-00000000000b";
        public const string ExternalIdTenantId = "cccccccc-0000-4000-8000-00000000000c";
        public const string ExternalIdInstance = "https://contoso.ciamlogin.com/";

        /// <summary>An Entra tenant this repository has never heard of, created by whoever wants one.</summary>
        public const string AttackerTenantId = "eeeeeeee-0000-4000-8000-00000000000e";

        public const string TenantXOwnerObjectId = "11111111-0000-4000-8000-000000000011";
        public const string TenantYManagerObjectId = "22222222-0000-4000-8000-000000000022";
        public const string ExternalIdMemberObjectId = "33333333-0000-4000-8000-000000000033";
        public const string ExternalIdStrangerObjectId = "44444444-0000-4000-8000-000000000044";

        /// <summary>
        /// The directory object the customer identity tenant created for a Google sign-up
        /// (issue #711). It belongs to no business, and shares a mailbox with the Tenant X Owner.
        /// </summary>
        public const string GoogleFederatedObjectId = "77777777-0000-4000-8000-000000000077";

        /// <summary>An upstream Google subject: opaque, not a GUID, and read by nothing here.</summary>
        public const string GoogleSubject = "108134655550123456789";

        /// <summary>
        /// One mailbox, reachable through a workforce account and through Google. It is display-only
        /// personal data in this application and decides nothing.
        /// </summary>
        public const string SharedMailbox = "dana@example.test";

        /// <summary>One object id that exists as a member in two different trusted tenants.</summary>
        public const string SharedObjectId = "55555555-0000-4000-8000-000000000055";

        /// <summary>
        /// The configured platform administrator's object id, in the workforce tenant and in no
        /// business.
        /// </summary>
        public const string PlatformAdminObjectId = "66666666-0000-4000-8000-000000000066";

        /// <summary>
        /// The one key set Microsoft publishes for the workforce authority, shared by every Entra
        /// tenant - an attacker's own tenant included.
        /// </summary>
        public static readonly SecurityKey WorkforceSigningKey = NewKey("workforce");

        /// <summary>The configured customer identity tenant's own key set.</summary>
        public static readonly SecurityKey ExternalIdSigningKey = NewKey("external-id");

        /// <summary>A key no trusted authority publishes.</summary>
        public static readonly SecurityKey UntrustedSigningKey = NewKey("untrusted");

        public static string ExternalIdIssuer => $"https://{ExternalIdTenantId}.ciamlogin.com/{ExternalIdTenantId}/v2.0";

        public static string WorkforceIssuer(string tenantId) => $"https://login.microsoftonline.com/{tenantId}/v2.0";

        public static OpenIdConnectConfiguration WorkforceMetadata =>
            Metadata("https://login.microsoftonline.com/{tenantid}/v2.0", WorkforceSigningKey);

        public static OpenIdConnectConfiguration ExternalIdMetadata =>
            Metadata(ExternalIdIssuer, ExternalIdSigningKey);

        public static string WorkforceAccessToken(string tenantId, string objectId) =>
            Token(WorkforceSigningKey, WorkforceIssuer(tenantId), WorkforceApiClientId, Claims(tenantId, objectId));

        public static string ExternalIdAccessToken(string objectId) =>
            Token(ExternalIdSigningKey, ExternalIdIssuer, ExternalIdApiClientId, Claims(ExternalIdTenantId, objectId));

        public static Dictionary<string, object> Claims(string tenantId, string objectId) =>
            new(StringComparer.Ordinal)
            {
                ["tid"] = tenantId,
                ["oid"] = objectId,
                ["scp"] = "access_as_user",
            };

        /// <summary>
        /// The same claims plus the display-only profile ones every real sign-in token carries
        /// (issue #524), including <see cref="SharedMailbox"/> - so a test can put one mailbox on
        /// two different identities and prove the address decides nothing.
        /// </summary>
        public static Dictionary<string, object> MailboxClaims(string tenantId, string objectId)
        {
            var claims = Claims(tenantId, objectId);
            claims["name"] = "Dana Operator";
            claims["preferred_username"] = SharedMailbox;
            claims["email"] = SharedMailbox;
            return claims;
        }

        public static string Token(
            SecurityKey signingKey,
            string? issuer,
            string? audience,
            Dictionary<string, object> claims,
            DateTime? notBefore = null,
            DateTime? expires = null)
        {
            var now = DateTime.UtcNow;

            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = now.AddMinutes(-5),
                NotBefore = notBefore ?? now.AddMinutes(-5),
                Expires = expires ?? now.AddMinutes(30),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            });
        }

        private static OpenIdConnectConfiguration Metadata(string issuer, SecurityKey signingKey)
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
            configuration.SigningKeys.Add(signingKey);
            return configuration;
        }

        private static SecurityKey NewKey(string keyId) =>
            new RsaSecurityKey(RSA.Create(2048)) { KeyId = $"inventoryapp-test-{keyId}" };
    }

    /// <summary>
    /// Hosts the real application with both schemes configured, over one in-memory relational
    /// SQLite database holding three businesses: one for each workforce tenant and one whose member
    /// signs in through the customer identity tenant. Only the two metadata documents are replaced;
    /// the schemes, the policy scheme that routes between them, the membership resolution, the
    /// query filters and the controllers are the production ones.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string TenantXBusinessName = "Tenant X Vending";
        public const string TenantYBusinessName = "Tenant Y Vending";
        public const string ExternalIdBusinessName = "Customer Identity Vending";

        private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly IConfigurationManager<OpenIdConnectConfiguration>? _customerIdentityMetadata;
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory() : this(null)
        {
        }

        /// <summary>
        /// Internal, not public: xUnit allows a class fixture exactly one public constructor, and
        /// only the provider-failure tests build a host whose customer identity metadata is
        /// something other than the configured tenant's.
        /// </summary>
        internal ApiFactory(IConfigurationManager<OpenIdConnectConfiguration>? customerIdentityMetadata)
        {
            E2ETestHostContentRoot.Pin();
            _customerIdentityMetadata = customerIdentityMetadata;
            _connection.Open();
        }

        public async Task SeedAsync()
        {
            _ = Services;

            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

            if (_seeded)
            {
                return;
            }

            db.Businesses.AddRange(
                Business(1, TenantXBusinessName),
                Business(2, TenantYBusinessName),
                Business(3, ExternalIdBusinessName));

            db.BusinessMemberships.AddRange(
                // This member's mailbox is also reachable through Google (issue #711), which is
                // what `A_google_identity_that_shares_a_members_mailbox_inherits_nothing_from_them`
                // presents from the customer identity tenant.
                Membership(
                    1,
                    TestTokens.WorkforceTenantX,
                    TestTokens.TenantXOwnerObjectId,
                    BusinessRole.Owner,
                    TestTokens.SharedMailbox),
                Membership(2, TestTokens.WorkforceTenantY, TestTokens.TenantYManagerObjectId, BusinessRole.Manager),
                Membership(3, TestTokens.ExternalIdTenantId, TestTokens.ExternalIdMemberObjectId, BusinessRole.Owner),
                // The same object id in two trusted tenants: two actors, two businesses, two roles.
                Membership(2, TestTokens.WorkforceTenantY, TestTokens.SharedObjectId, BusinessRole.Operator),
                Membership(3, TestTokens.ExternalIdTenantId, TestTokens.SharedObjectId, BusinessRole.Manager));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "common");
            builder.UseSetting("AzureAd:ClientId", TestTokens.WorkforceApiClientId);
            builder.UseSetting("AzureAd:Scopes", "access_as_user");
            builder.UseSetting("ExternalId:Instance", TestTokens.ExternalIdInstance);
            builder.UseSetting("ExternalId:TenantId", TestTokens.ExternalIdTenantId);
            builder.UseSetting("ExternalId:ClientId", TestTokens.ExternalIdApiClientId);
            builder.UseSetting("ExternalId:Scopes", "access_as_user");

            // One configured platform administrator, in the workforce tenant and in no business
            // (design § 8), so the platform-admin surface is asserted against a real pair rather
            // than against the shipped "nobody is an administrator" state.
            builder.UseSetting("PlatformAdmin:DirectoryTenantId", TestTokens.WorkforceTenantX);
            builder.UseSetting("PlatformAdmin:ObjectId", TestTokens.PlatformAdminObjectId);

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            // The only thing replaced about authentication: where each scheme reads its issuer and
            // signing keys from. Nothing else - no validation rule, no claim, no scheme - is
            // substituted, and neither document is reachable from production code.
            builder.ConfigureTestServices(services =>
            {
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(TestTokens.WorkforceMetadata));

                services.Configure<JwtBearerOptions>(
                    ExternalIdBearerScheme.Name,
                    options => options.ConfigurationManager = _customerIdentityMetadata
                        ?? new StaticConfigurationManager<OpenIdConnectConfiguration>(TestTokens.ExternalIdMetadata));
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

        private static Business Business(int id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            };

        /// <summary>
        /// How many membership rows exist for one identity, read outside the tenant filters. It is
        /// what proves nothing was *linked*: a Google identity sharing a member's mailbox must not
        /// gain a membership by presenting a token (issue #711).
        /// </summary>
        public async Task<int> MembershipCountAsync(string directoryTenantId, string objectId)
        {
            await using var db = TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

            return await db.BusinessMemberships.CountAsync(
                membership => membership.DirectoryTenantId == directoryTenantId && membership.ObjectId == objectId);
        }

        private static BusinessMembership Membership(
            int businessId, string directoryTenantId, string objectId, BusinessRole role, string? email = null) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = directoryTenantId,
                ObjectId = objectId,
                Email = email,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
            };
    }
}
