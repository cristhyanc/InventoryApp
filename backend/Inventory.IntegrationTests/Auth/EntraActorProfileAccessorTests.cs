using System.Security.Claims;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Turning the validated token's display claims into a <see cref="MemberProfile"/> (issue #524).
///
/// The accessor is the second claims adapter at this boundary, and what these tests pin down is how
/// strictly it stays the junior of the two: it reads two display claims and takes the identity from
/// <see cref="EntraActorIdentityAccessor"/>, so a token whose <c>(tid, oid)</c> pair identifies
/// nobody yields no profile at all - a name and an email address can never stand in for an
/// identity.
/// </summary>
public class EntraActorProfileAccessorTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static EntraActorProfileAccessor Accessor(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        if (claims.Length > 0)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
        }

        var accessor = new HttpContextAccessor { HttpContext = context };

        return new EntraActorProfileAccessor(new EntraActorIdentityAccessor(accessor), accessor);
    }

    private static Claim[] Identity() => [new Claim("tid", Tid), new Claim("oid", Oid)];

    private static Claim[] With(params Claim[] claims) => [.. Identity(), .. claims];

    [Fact]
    public void The_name_and_preferred_username_claims_become_the_members_profile()
    {
        var actor = Accessor(With(
            new Claim("name", "Ada Lovelace"),
            new Claim("preferred_username", "ada@vending.example"))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Equal("Ada Lovelace", actor.Profile.DisplayName);
        Assert.Equal("ada@vending.example", actor.Profile.Email);
        Assert.Equal(Tid, actor.Identity.DirectoryTenantId);
        Assert.Equal(Oid, actor.Identity.ObjectId);
    }

    /// <summary>
    /// The WS-* mapped spellings a host with inbound claim mapping on produces are read too, for
    /// the same reason the identity accessor accepts both spellings of <c>tid</c> and <c>oid</c>:
    /// which spelling arrives is a host configuration detail, not a different fact.
    /// </summary>
    [Fact]
    public void The_mapped_claim_spellings_are_read_as_well()
    {
        var actor = Accessor(With(
            new Claim(ClaimTypes.Name, "Ada Lovelace"),
            new Claim(ClaimTypes.Email, "ada@vending.example"))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Equal("Ada Lovelace", actor.Profile.DisplayName);
        Assert.Equal("ada@vending.example", actor.Profile.Email);
    }

    /// <summary>
    /// <c>email</c> is the optional claim a tenant may issue instead of <c>preferred_username</c>,
    /// and is used when that one is absent.
    /// </summary>
    [Fact]
    public void The_email_claim_is_used_when_there_is_no_preferred_username()
    {
        var actor = Accessor(With(new Claim("email", "ada@vending.example"))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Equal("ada@vending.example", actor.Profile.Email);
        Assert.Null(actor.Profile.DisplayName);
    }

    /// <summary>
    /// A token carrying both spellings carries the same address twice, so the first one in
    /// preference order wins rather than the two being merged or compared.
    /// </summary>
    [Fact]
    public void Preferred_username_wins_over_the_email_claim()
    {
        var actor = Accessor(With(
            new Claim("preferred_username", "ada@vending.example"),
            new Claim("email", "ada.lovelace@elsewhere.example"))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Equal("ada@vending.example", actor.Profile.Email);
    }

    /// <summary>
    /// The missing-claims case: an identified actor whose token says nothing about their name or
    /// email address is still an identified actor, and the profile is simply empty - which is what
    /// stops anything being written for them.
    /// </summary>
    [Fact]
    public void An_identified_actor_whose_token_carries_no_display_claims_has_an_empty_profile()
    {
        var actor = Accessor(Identity()).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Equal(MemberProfile.None, actor.Profile);
        Assert.False(actor.Profile.HasAnyValue);
    }

    [Fact]
    public void Blank_display_claims_are_the_same_as_absent_ones()
    {
        var actor = Accessor(With(
            new Claim("name", "   "),
            new Claim("preferred_username", ""))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.False(actor.Profile.HasAnyValue);
    }

    /// <summary>
    /// An unauthenticated request has no profile to read, and neither has a request whose token
    /// cannot identify an actor - here a malformed <c>oid</c>. The display claims are deliberately
    /// not a fallback: a membership row is found by the <c>(tid, oid)</c> pair alone.
    /// </summary>
    [Fact]
    public void A_request_with_no_identifiable_actor_has_no_profile()
    {
        Assert.Null(Accessor().GetCurrentActorProfile());

        Assert.Null(Accessor(
            new Claim("tid", Tid),
            new Claim("oid", "not-a-guid"),
            new Claim("name", "Ada Lovelace"),
            new Claim("preferred_username", "ada@vending.example")).GetCurrentActorProfile());
    }

    /// <summary>
    /// The length bound is applied to what a token claim can write into the row, not only to what a
    /// person might type: an absurdly long claim value is dropped rather than stored.
    /// </summary>
    [Fact]
    public void An_over_long_claim_value_is_dropped()
    {
        var actor = Accessor(With(
            new Claim("name", new string('a', MemberProfile.MaxLength + 1)),
            new Claim("preferred_username", "ada@vending.example"))).GetCurrentActorProfile();

        Assert.NotNull(actor);
        Assert.Null(actor.Profile.DisplayName);
        Assert.Equal("ada@vending.example", actor.Profile.Email);
    }
}
