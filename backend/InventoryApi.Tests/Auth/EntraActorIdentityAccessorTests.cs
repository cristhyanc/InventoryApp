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
