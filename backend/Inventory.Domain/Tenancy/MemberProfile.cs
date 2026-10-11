namespace Inventory.Domain.Tenancy;

/// <summary>
/// A member's display-only name and email address, as the validated sign-in token reported them
/// (issue #524).
///
/// This type is the opposite of <see cref="ActorIdentity"/> and must stay that way. An identity
/// decides what a caller may read; a profile decides nothing at all. Nothing in the application
/// resolves a business, a membership, a role or a capability from either value, and neither is ever
/// logged: a name and an email address are mutable, reassignable and personal, which is exactly why
/// issue #64 made the <c>(tid, oid)</c> pair the identity and why these two are kept out of every
/// access decision.
///
/// Both values are optional, because a validated token may carry neither claim. "Absent" is
/// deliberately one state rather than two: a missing claim, a blank one and a claim this
/// application will not store all produce <c>null</c>, so a member whose token says nothing about
/// them simply has nothing stored.
/// </summary>
public sealed record MemberProfile
{
    /// <summary>
    /// The longest value either half accepts. A display name or an email address longer than this
    /// is not a display value anybody meant; the limit bounds what one token claim can write into
    /// the membership row.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>A token that reported neither claim.</summary>
    public static MemberProfile None { get; } = new(null, null);

    private MemberProfile(string? displayName, string? email)
    {
        DisplayName = displayName;
        Email = email;
    }

    /// <summary>The member's display name, or <c>null</c> when the token reported none.</summary>
    public string? DisplayName { get; }

    /// <summary>The member's email address, or <c>null</c> when the token reported none.</summary>
    public string? Email { get; }

    /// <summary>
    /// Whether there is anything worth storing. A profile with neither value is never written and
    /// never consumes the hourly refresh: a token that says nothing must not stop the next one that
    /// does from being stored.
    /// </summary>
    public bool HasAnyValue => DisplayName is not null || Email is not null;

    /// <summary>
    /// Builds a profile from two raw claim values, each trimmed, with blank treated as absent.
    ///
    /// A value longer than <see cref="MaxLength"/> is treated as absent too, rather than truncated.
    /// Truncating is the worse answer for both halves: a cut-off email address is a wrong email
    /// address, and half of somebody's name displayed as if it were their name is wrong in the same
    /// way. Storing nothing displays nothing, which is honest about what the token gave us.
    /// </summary>
    public static MemberProfile Create(string? displayName, string? email)
    {
        var name = Normalize(displayName);
        var address = Normalize(email);

        return name is null && address is null ? None : new MemberProfile(name, address);
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength ? null : trimmed;
    }
}
