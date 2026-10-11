using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// The write half of the member-profile refresh (issue #524): it asks the narrow write path to
/// bring one identity's own active membership up to date, and reports what happened without
/// carrying either value anywhere else.
/// </summary>
public class RefreshMemberProfileTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static ActorIdentity Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tid, Oid, out var actor));
        return actor!;
    }

    private static MemberProfile Profile() => MemberProfile.Create("Ada Lovelace", "ada@vending.example");

    [Fact]
    public async Task The_claims_are_written_for_the_authenticated_identity()
    {
        var store = new FakeMemberProfileWriteStore(rowsChanged: 1);

        var outcome = await new RefreshMemberProfile(store).Handle(Actor(), Profile(), CancellationToken.None);

        Assert.Equal(MemberProfileRefreshOutcome.Refreshed, outcome);
        var write = Assert.Single(store.Writes);
        Assert.Equal(Actor(), write.Identity);
        Assert.Equal(Profile(), write.Profile);
    }

    /// <summary>
    /// No row changed is an ordinary outcome, not a failure: the stored profile already matched the
    /// token, or the identity holds no active membership - a membership revoked between the request
    /// and the drain, for instance.
    /// </summary>
    [Fact]
    public async Task No_row_changed_is_reported_rather_than_treated_as_a_failure()
    {
        var store = new FakeMemberProfileWriteStore(rowsChanged: 0);

        var outcome = await new RefreshMemberProfile(store).Handle(Actor(), Profile(), CancellationToken.None);

        Assert.Equal(MemberProfileRefreshOutcome.NoRowChanged, outcome);
        Assert.Single(store.Writes);
    }

    /// <summary>
    /// A profile with nothing in it never reaches the database at all: the use case is the last
    /// thing before the write, so the rule holds whoever called it.
    /// </summary>
    [Fact]
    public async Task A_profile_with_neither_value_writes_nothing()
    {
        var store = new FakeMemberProfileWriteStore();

        var outcome = await new RefreshMemberProfile(store).Handle(Actor(), MemberProfile.None, CancellationToken.None);

        Assert.Equal(MemberProfileRefreshOutcome.NothingToStore, outcome);
        Assert.Empty(store.Writes);
    }
}
