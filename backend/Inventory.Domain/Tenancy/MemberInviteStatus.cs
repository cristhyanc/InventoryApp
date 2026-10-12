namespace Inventory.Domain.Tenancy;

/// <summary>
/// What one member invite is, as the agreed contract of issue #531 (one slice of #504 under #328)
/// names the four states. This is the complete list: there is no "other" and no state derived from
/// anything but these values and the invite's own expiry instant.
///
/// The values are spaced, exactly as <see cref="BusinessRole"/>'s and
/// <see cref="BusinessStatus"/>'s are, so a further state can be declared between two of them
/// without renumbering a value that is already persisted, and <c>0</c> is deliberately not a
/// declared state, because <c>0</c> is what an unfilled integer column and a default-constructed
/// enum both hold.
///
/// <para><strong><see cref="Expired"/> is never written.</strong> It is derived from the invite's
/// <c>ExpiresAtUtc</c> by <see cref="MemberInvitePolicy.EffectiveStatus"/>, so an invite stops being
/// usable the instant it expires with no background job, no startup sweep and no row to update. A
/// stored status is therefore only ever <see cref="Pending"/>, <see cref="Used"/> or
/// <see cref="Cancelled"/>; the state is declared here all the same, because it is a state the API
/// reports and the one thing a client must be able to tell from a cancellation.</para>
/// </summary>
public enum MemberInviteStatus
{
    /// <summary>Created, not yet used, not cancelled, and not past its expiry.</summary>
    Pending = 10,

    /// <summary>Redeemed: it created the membership it names, and it can never be used again.</summary>
    Used = 20,

    /// <summary>
    /// Past its expiry while still pending. Derived from the expiry instant and never stored; see
    /// the remarks on this enum.
    /// </summary>
    Expired = 30,

    /// <summary>Withdrawn by an Owner, or closed with the business, before it was used.</summary>
    Cancelled = 40,
}
