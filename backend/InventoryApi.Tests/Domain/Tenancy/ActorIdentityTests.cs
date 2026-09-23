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
