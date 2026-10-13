using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic invite rules of issue #531: the seven-day lifetime, the state an invite is in
/// at a given instant, and what a submitted note may be.
///
/// The expiry derivation is the part worth testing hardest. It is what makes an invite stop working
/// with no sweep, no job and no row to update, so every read path has to agree on it - and the one
/// state it must never produce for an unusable invite is <c>Pending</c>.
/// </summary>
public class MemberInvitePolicyTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_invite_lasts_seven_days_from_its_creation()
    {
        Assert.Equal(TimeSpan.FromDays(7), MemberInvitePolicy.Lifetime);
        Assert.Equal(new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc), MemberInvitePolicy.ExpiresAt(CreatedAt));
    }

    /// <summary>
    /// A pending invite is pending right up to its expiry instant, and expired at it. Seven days of
    /// validity is the promise, and a link that still worked on the first tick of the eighth day
    /// would have outlived it.
    /// </summary>
    [Fact]
    public void A_pending_invite_expires_at_its_expiry_instant_and_not_a_tick_later()
    {
        var expiresAt = MemberInvitePolicy.ExpiresAt(CreatedAt);

        Assert.Equal(
            MemberInviteStatus.Pending,
            MemberInvitePolicy.EffectiveStatus(MemberInviteStatus.Pending, expiresAt, expiresAt.AddTicks(-1)));
        Assert.Equal(
            MemberInviteStatus.Expired,
            MemberInvitePolicy.EffectiveStatus(MemberInviteStatus.Pending, expiresAt, expiresAt));
        Assert.Equal(
            MemberInviteStatus.Expired,
            MemberInvitePolicy.EffectiveStatus(MemberInviteStatus.Pending, expiresAt, expiresAt.AddDays(30)));

        Assert.True(MemberInvitePolicy.IsLive(MemberInviteStatus.Pending, expiresAt, expiresAt.AddTicks(-1)));
        Assert.False(MemberInvitePolicy.IsLive(MemberInviteStatus.Pending, expiresAt, expiresAt));
    }

    /// <summary>
    /// A used or cancelled invite is reported as what it is, expiry or no expiry: the expiry only
    /// ever decides a <em>pending</em> invite, so an invite redeemed a year ago does not start
    /// reporting itself as merely expired.
    /// </summary>
    [Theory]
    [InlineData(MemberInviteStatus.Used)]
    [InlineData(MemberInviteStatus.Cancelled)]
    public void A_closed_invite_keeps_its_own_state_past_the_expiry(MemberInviteStatus stored)
    {
        var expiresAt = MemberInvitePolicy.ExpiresAt(CreatedAt);

        Assert.Equal(stored, MemberInvitePolicy.EffectiveStatus(stored, expiresAt, CreatedAt));
        Assert.Equal(stored, MemberInvitePolicy.EffectiveStatus(stored, expiresAt, expiresAt.AddDays(365)));
        Assert.False(MemberInvitePolicy.IsLive(stored, expiresAt, CreatedAt));
    }

    /// <summary>
    /// A stored state this code does not declare - <c>0</c> from a column an older schema never
    /// filled, or a value a newer deployment wrote - is reported as unknown and is never live. The
    /// fail-closed direction matters here: reading it as pending would make an invite nobody
    /// recorded look usable.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(-5)]
    public void An_undeclared_stored_state_is_unknown_and_never_live(int stored)
    {
        var status = (MemberInviteStatus)stored;
        var expiresAt = MemberInvitePolicy.ExpiresAt(CreatedAt);

        Assert.False(MemberInviteStatuses.IsSupported(status));
        Assert.Null(MemberInvitePolicy.EffectiveStatus(status, expiresAt, CreatedAt));
        Assert.False(MemberInvitePolicy.IsLive(status, expiresAt, CreatedAt));
    }

    [Fact]
    public void The_declared_states_are_the_four_agreed_ones()
    {
        Assert.Equal(
            [
                MemberInviteStatus.Pending,
                MemberInviteStatus.Used,
                MemberInviteStatus.Expired,
                MemberInviteStatus.Cancelled,
            ],
            MemberInviteStatuses.All);
        Assert.All(MemberInviteStatuses.All, status => Assert.True(MemberInviteStatuses.IsSupported(status)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_blank_note_normalises_to_no_note_at_all(string? note) =>
        Assert.Null(MemberInvitePolicy.NormalizeNote(note));

    [Fact]
    public void A_note_is_trimmed_and_may_be_as_long_as_the_limit()
    {
        Assert.Equal("Tuesday run", MemberInvitePolicy.NormalizeNote("  Tuesday run  "));

        var longest = new string('n', MemberInvitePolicy.MaxNoteLength);
        Assert.Equal(longest, MemberInvitePolicy.NormalizeNote(longest));
        // The bound is measured after trimming, so surrounding whitespace is not what refuses it.
        Assert.Equal(longest, MemberInvitePolicy.NormalizeNote($"  {longest}  "));
    }

    [Fact]
    public void A_note_longer_than_the_limit_is_refused_and_never_echoed()
    {
        var note = new string('n', MemberInvitePolicy.MaxNoteLength + 1);

        var refusal = Assert.Throws<DomainValidationException>(() => MemberInvitePolicy.NormalizeNote(note));

        Assert.Equal(MemberInvitePolicy.NoteTooLongMessage, refusal.Message);
        Assert.DoesNotContain(note, refusal.Message, StringComparison.Ordinal);
    }
}
