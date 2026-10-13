using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The deterministic rules of a support session (issue #550): the fixed lifetime, which business one
/// may be started for, when a stored one stops being active, and what a submitted reason may be.
/// </summary>
public class SupportSessionPolicyTests
{
    private static readonly DateTime StartedAt = new(2026, 10, 13, 2, 15, 0, DateTimeKind.Utc);

    /// <summary>
    /// The lifetime is the agreed 60 minutes, which is both the default and the server maximum. It
    /// is asserted rather than assumed because it is the one number that decides how long somebody
    /// else's data stays readable, and no request may choose or extend it.
    /// </summary>
    [Fact]
    public void A_session_lasts_sixty_minutes_from_its_start()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), SupportSessionPolicy.Lifetime);
        Assert.Equal(StartedAt.AddMinutes(60), SupportSessionPolicy.ExpiresAt(StartedAt));
    }

    /// <summary>
    /// Only a trading business may be read through a support session. A pending application, a
    /// refused one, a deactivated business, an unfilled column reading <c>0</c> and a value a newer
    /// deployment wrote are each refused: a state nobody declared is not a state anything may be
    /// decided from.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Active, true)]
    [InlineData(BusinessStatus.Pending, false)]
    [InlineData(BusinessStatus.Rejected, false)]
    [InlineData(BusinessStatus.Deactivated, false)]
    [InlineData((BusinessStatus)0, false)]
    [InlineData((BusinessStatus)99, false)]
    public void A_session_may_be_started_only_for_an_active_business(BusinessStatus status, bool allowed) =>
        Assert.Equal(allowed, SupportSessionPolicy.CanStartFor(status));

    /// <summary>
    /// A session is active until - but not at - its expiry, and an ended one is never active again
    /// whatever its expiry says. The inclusive boundary is the promise: sixty minutes of access, not
    /// sixty minutes and a tick.
    /// </summary>
    [Fact]
    public void A_session_is_active_until_its_expiry_and_never_after_it_was_ended()
    {
        var expiresAt = SupportSessionPolicy.ExpiresAt(StartedAt);

        Assert.True(SupportSessionPolicy.IsActive(expiresAt, endedAtUtc: null, StartedAt));
        Assert.True(SupportSessionPolicy.IsActive(expiresAt, endedAtUtc: null, expiresAt.AddTicks(-1)));
        Assert.False(SupportSessionPolicy.IsActive(expiresAt, endedAtUtc: null, expiresAt));
        Assert.False(SupportSessionPolicy.IsActive(expiresAt, endedAtUtc: null, expiresAt.AddMinutes(1)));

        // Ended, and therefore over - including when asked about an instant it was still running at,
        // which is how a session ended by a deactivation stops being usable immediately.
        Assert.False(SupportSessionPolicy.IsActive(expiresAt, StartedAt.AddMinutes(5), StartedAt));
    }

    /// <summary>
    /// A reason is required: a support session is read access to somebody else's business, so the
    /// trail must say why. A missing, empty or whitespace-only value is the same refusal, so a blank
    /// field cannot stand in for an explanation.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_reason_is_required(string? reason)
    {
        var invalid = Assert.Throws<DomainValidationException>(() => SupportSessionPolicy.RequireReason(reason));

        Assert.Equal(SupportSessionPolicy.ReasonRequiredMessage, invalid.Message);
    }

    /// <summary>The stored reason is trimmed, so one reason has exactly one representation.</summary>
    [Fact]
    public void A_reason_is_stored_trimmed() =>
        Assert.Equal("Dashboard error", SupportSessionPolicy.RequireReason("  Dashboard error\t"));

    /// <summary>
    /// The bound is on the trimmed value and the refusal never echoes the reason back - a 500-
    /// character value is accepted, and the first character past it is refused.
    /// </summary>
    [Fact]
    public void A_reason_is_bounded_at_five_hundred_characters()
    {
        var longest = new string('x', SupportSessionPolicy.MaxReasonLength);

        Assert.Equal(longest, SupportSessionPolicy.RequireReason($" {longest} "));

        var invalid = Assert.Throws<DomainValidationException>(
            () => SupportSessionPolicy.RequireReason(longest + "x"));

        Assert.Equal(SupportSessionPolicy.ReasonTooLongMessage, invalid.Message);
        Assert.DoesNotContain(longest, invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The session id is a credential: it is at least the agreed 128 bits of entropy, this
    /// deployment draws 256, and the stored form is a one-way SHA-256 digest that is deterministic
    /// (so a presented id can be matched) and reveals nothing (so a copy of the database is useless).
    /// </summary>
    [Fact]
    public void The_session_id_is_stored_only_as_a_deterministic_one_way_hash()
    {
        Assert.True(SupportSessionId.EntropyBytes >= SupportSessionId.MinimumEntropyBytes);
        Assert.Equal(32, SupportSessionId.EntropyBytes);

        var hash = SupportSessionId.Hash("a-session-id-nobody-else-sees");

        Assert.Equal(SupportSessionId.HashLength, hash.Length);
        Assert.Equal(hash, SupportSessionId.Hash("a-session-id-nobody-else-sees"));
        Assert.NotEqual(hash, SupportSessionId.Hash("a-session-id-nobody-else-seeS"));
        Assert.DoesNotContain("a-session-id", hash, StringComparison.Ordinal);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }

    /// <summary>
    /// Hashing nothing is a programming error, not a caller's input: a lookup that reached the hash
    /// with an empty value would otherwise turn the empty string into a perfectly valid-looking key.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hashing_a_blank_session_id_is_refused(string sessionId) =>
        Assert.Throws<ArgumentException>(() => SupportSessionId.Hash(sessionId));

    [Fact]
    public void Hashing_a_null_session_id_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => SupportSessionId.Hash(null!));
}
