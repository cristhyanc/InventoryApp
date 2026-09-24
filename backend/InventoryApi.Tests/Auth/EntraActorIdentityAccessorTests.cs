using System.Security.Claims;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Claims parsing is the one thing that stays in InventoryApi (issue #64), so this is where the
/// Entra token shape is pinned down: the validated <c>(tid, oid)</c> pair identifies the actor,
/// in either the short JWT spelling or the mapped schemas.microsoft.com URI, and nothing else
/// does.
/// </summary>
public class EntraActorIdentityAccessorTests
{
    private const string TenantIdUriClaim = "http://schemas.microsoft.com/identity/claims/tenantid";
    private const string ObjectIdUriClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static EntraActorIdentityAccessor CreateAccessor(HttpContext? httpContext)
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        return new EntraActorIdentityAccessor(httpContextAccessor);
    }

    private static HttpContext AuthenticatedContext(params Claim[] claims) =>
        new DefaultHttpContext
        {
            // A non-empty authentication type is what makes ClaimsIdentity report IsAuthenticated.
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")),
        };

    [Theory]
    [InlineData("tid", "oid")]
    [InlineData(TenantIdUriClaim, ObjectIdUriClaim)]
    public void Both_claim_spellings_identify_the_actor(string tenantClaimType, string objectIdClaimType)
    {
        var accessor = CreateAccessor(AuthenticatedContext(
            new Claim(tenantClaimType, Tid),
            new Claim(objectIdClaimType, Oid)));

        var result = accessor.GetCurrentActor();

        Assert.Null(result.DenialReason);
        Assert.NotNull(result.Actor);
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var expected));
        Assert.Equal(expected, result.Actor);
    }

    [Fact]
    public void No_http_context_is_reported_as_not_authenticated()
    {
        var result = CreateAccessor(null).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.NotAuthenticated, result.DenialReason);
    }

    [Fact]
    public void Anonymous_request_is_reported_as_not_authenticated()
    {
        // An identity with no authentication type is the anonymous principal ASP.NET Core
        // installs before authentication middleware has authenticated anyone.
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        var result = CreateAccessor(httpContext).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.NotAuthenticated, result.DenialReason);
    }

    /// <summary>
    /// A validated token that carries only one half of the pair identifies no actor. The
    /// accessor must not substitute a weaker identifier, so the request fails closed.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_token_missing_either_half_of_the_pair_is_unidentifiable(bool includeTid, bool includeOid)
    {
        var claims = new List<Claim>();
        if (includeTid) claims.Add(new Claim("tid", Tid));
        if (includeOid) claims.Add(new Claim("oid", Oid));

        var result = CreateAccessor(AuthenticatedContext([.. claims])).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    /// <summary>
    /// Email, display name, and subject are mutable or issuer-scoped, so a token carrying only
    /// those must still be unidentifiable - they must never become an ownership key.
    /// </summary>
    [Fact]
    public void Email_name_and_subject_claims_do_not_identify_an_actor()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("preferred_username", "someone@example.com"),
            new Claim(ClaimTypes.Email, "someone@example.com"),
            new Claim(ClaimTypes.Name, "Someone"),
            new Claim("sub", "abc123"))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    /// <summary>
    /// The same GUID supplied through both supported spellings is not a conflict - it is the
    /// ordinary shape of a token when claim mapping is partially applied - and must still
    /// identify the actor.
    /// </summary>
    [Fact]
    public void Identical_values_through_both_claim_spellings_identify_the_actor()
    {
        var accessor = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim(TenantIdUriClaim, Tid),
            new Claim("oid", Oid),
            new Claim(ObjectIdUriClaim, Oid)));

        var result = accessor.GetCurrentActor();

        Assert.Null(result.DenialReason);
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var expected));
        Assert.Equal(expected, result.Actor);
    }

    /// <summary>
    /// Casing and equivalent GUID spellings are not a conflict either: both claims denote one
    /// identifier, so the actor is still identified.
    /// </summary>
    [Fact]
    public void Equivalent_guid_spellings_across_claim_types_are_not_a_conflict()
    {
        var accessor = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid.ToUpperInvariant()),
            new Claim(TenantIdUriClaim, $"{{{Tid}}}"),
            new Claim("oid", Oid),
            new Claim(ObjectIdUriClaim, Oid.Replace("-", string.Empty, StringComparison.Ordinal))));

        var result = accessor.GetCurrentActor();

        Assert.Null(result.DenialReason);
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var expected));
        Assert.Equal(expected, result.Actor);
    }

    /// <summary>
    /// Two recognized spellings naming different directories. Preferring whichever was checked
    /// first would let the choice of claim spelling decide whose data the caller reads, so the
    /// request fails closed instead.
    /// </summary>
    [Fact]
    public void Conflicting_short_and_uri_tenant_id_claims_are_unidentifiable()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim(TenantIdUriClaim, "99999999-9999-9999-9999-999999999999"),
            new Claim("oid", Oid))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    /// <summary>Two recognized spellings naming different actors: same fail-closed rule.</summary>
    [Fact]
    public void Conflicting_short_and_uri_object_id_claims_are_unidentifiable()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim("oid", Oid),
            new Claim(ObjectIdUriClaim, "99999999-9999-9999-9999-999999999999"))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    /// <summary>Repeated claims of one spelling must agree too, not only across spellings.</summary>
    [Fact]
    public void Repeated_conflicting_claims_of_the_same_spelling_are_unidentifiable()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim("tid", "99999999-9999-9999-9999-999999999999"),
            new Claim("oid", Oid))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("someone@example.com")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    public void A_malformed_tenant_id_claim_is_unidentifiable(string malformedTid)
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", malformedTid),
            new Claim("oid", Oid))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("someone@example.com")]
    [InlineData("22222222-2222-2222-2222-22222222222")]
    public void A_malformed_object_id_claim_is_unidentifiable(string malformedOid)
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim("oid", malformedOid))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    /// <summary>
    /// A malformed value in one spelling is not rescued by a well-formed value in the other:
    /// the token is inconsistent, so it identifies no actor.
    /// </summary>
    [Fact]
    public void A_malformed_value_alongside_a_valid_one_is_unidentifiable()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", Tid),
            new Claim(TenantIdUriClaim, "not-a-guid"),
            new Claim("oid", Oid))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }

    [Fact]
    public void Blank_claim_values_are_unidentifiable()
    {
        var result = CreateAccessor(AuthenticatedContext(
            new Claim("tid", "   "),
            new Claim("oid", "   "))).GetCurrentActor();

        Assert.Null(result.Actor);
        Assert.Equal(BusinessAccessDenialReason.UnidentifiableActor, result.DenialReason);
    }
}
