using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The actor identity is the Entra <c>(tid, oid)</c> pair and nothing else (issue #64). Both
/// halves are mandatory: a half-identified caller must be denied, never resolved from a weaker
/// substitute such as email or the directory tenant alone.
/// </summary>
public class ActorIdentityTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Both_claim_values_produce_an_identity()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var identity));

        Assert.NotNull(identity);
        Assert.Equal(Tid.ToUpperInvariant(), identity.DirectoryTenantId);
        Assert.Equal(Oid.ToUpperInvariant(), identity.ObjectId);
    }

    [Theory]
    [InlineData(null, Oid)]
    [InlineData(Tid, null)]
    [InlineData("", Oid)]
    [InlineData(Tid, "")]
    [InlineData("   ", Oid)]
    [InlineData(Tid, "   ")]
    [InlineData(null, null)]
    public void A_missing_or_blank_half_yields_no_identity(string? directoryTenantId, string? objectId)
    {
        Assert.False(ActorIdentity.TryCreate(directoryTenantId, objectId, out var identity));

        Assert.Null(identity);
    }

    /// <summary>
    /// tid and oid are Entra GUID identifiers. A value that is not a GUID is not an Entra
    /// identifier, so it must be rejected outright rather than carried into a membership lookup
    /// as an opaque string.
    /// </summary>
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("someone@example.com")]
    [InlineData("12345")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    [InlineData("11111111-1111-1111-1111-1111111111111")]
    [InlineData("11111111-1111-1111-1111-11111111111g")]
    [InlineData("11111111111111111111111111111111'--")]
    public void A_malformed_directory_tenant_id_is_rejected(string malformedTid)
    {
        Assert.False(ActorIdentity.TryCreate(malformedTid, Oid, out var identity));

        Assert.Null(identity);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("someone@example.com")]
    [InlineData("12345")]
    [InlineData("22222222-2222-2222-2222-22222222222")]
    [InlineData("22222222-2222-2222-2222-2222222222222")]
    [InlineData("22222222-2222-2222-2222-22222222222g")]
    [InlineData("22222222222222222222222222222222'--")]
    public void A_malformed_object_id_is_rejected(string malformedOid)
    {
        Assert.False(ActorIdentity.TryCreate(Tid, malformedOid, out var identity));

        Assert.Null(identity);
    }

    /// <summary>
    /// The braced and dashless GUID spellings denote the same identifier, so they must normalize
    /// to the same comparison key rather than producing a second, unmatchable identity.
    /// </summary>
    [Theory]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    public void Equivalent_guid_spellings_normalize_to_the_same_identity(string equivalentTid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var canonical));
        Assert.True(ActorIdentity.TryCreate(equivalentTid, Oid, out var variant));

        Assert.Equal(canonical, variant);
    }

    /// <summary>
    /// Entra returns GUID claim values whose casing and surrounding whitespace are not
    /// significant; a membership lookup must not depend on them.
    /// </summary>
    [Fact]
    public void Casing_and_surrounding_whitespace_do_not_change_the_identity()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var canonical));
        Assert.True(ActorIdentity.TryCreate($"  {Tid.ToUpperInvariant()} ", $" {Oid.ToUpperInvariant()}  ", out var variant));

        Assert.Equal(canonical, variant);
    }

    /// <summary>
    /// Two actors from different directories that happen to share an object id are different
    /// actors: the pair identifies, neither half does.
    /// </summary>
    [Fact]
    public void Same_object_id_in_a_different_directory_is_a_different_actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var first));
        Assert.True(ActorIdentity.TryCreate("33333333-3333-3333-3333-333333333333", Oid, out var second));

        Assert.NotEqual(first, second);
    }
}
