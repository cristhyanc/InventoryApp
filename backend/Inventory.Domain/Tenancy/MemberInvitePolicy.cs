using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of a member invite (issue #531): how long one lasts, what state it is in
/// at a given instant, and what a submitted note may be. No clock, no database and no configuration
/// - every answer is a function of the values handed in, which is what lets the same rule decide a
/// list, a cancellation and (later, under #532) a redemption.
/// </summary>
public static class MemberInvitePolicy
{
    /// <summary>
    /// How long an invite is usable for: seven days from its creation, as #328's agreed contract
    /// fixes it. It is a constant rather than a setting because an invite's lifetime is a security
    /// property of the link, not something a business configures - and a configurable one would be
    /// a way to create a link that never expires.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest note an invite may carry. It is a bound on a value a person types into a form,
    /// not a meaningful business limit: the note exists so an Owner can recognise their own invite
    /// in a list, and a value this long is a paste accident.
    /// </summary>
    public const int MaxNoteLength = 200;

    /// <summary>The message an over-long note is refused with. It never echoes the note back.</summary>
    public const string NoteTooLongMessage =
        "An invite note must be no longer than 200 characters.";

    /// <summary>When an invite created at <paramref name="createdAtUtc"/> stops being usable.</summary>
    public static DateTime ExpiresAt(DateTime createdAtUtc) => createdAtUtc + Lifetime;

    /// <summary>
    /// The state an invite is in at <paramref name="nowUtc"/>: its stored state, except that a
    /// stored <see cref="MemberInviteStatus.Pending"/> whose expiry has passed is
    /// <see cref="MemberInviteStatus.Expired"/>.
    ///
    /// <para>The expiry is deliberately derived rather than stored, so an invite stops being usable
    /// at the instant it expires, on every read path at once, with no sweep to run and no row that
    /// could be left claiming an expired invite is still pending.</para>
    ///
    /// <para>The boundary is inclusive of the expiry instant: an invite is expired *at*
    /// <c>ExpiresAtUtc</c>, not one tick later. Seven days of validity is the promise, and a link
    /// that works on the first tick of the eighth day is a link that outlived it.</para>
    /// </summary>
    /// <param name="storedStatus">The status as persistence read it, declared or not.</param>
    /// <param name="expiresAtUtc">The invite's own expiry instant.</param>
    /// <param name="nowUtc">The instant to judge it at.</param>
    /// <returns>
    /// The effective state, or <see langword="null"/> when <paramref name="storedStatus"/> is a
    /// value this code does not declare - a hand-written <c>UPDATE</c>, or a state a newer
    /// deployment wrote. Such an invite is reported as unknown rather than guessed at, and is never
    /// reported as pending: naming a state nobody recorded is how an invite comes to look usable
    /// when nothing says it is.
    /// </returns>
    public static MemberInviteStatus? EffectiveStatus(
        MemberInviteStatus storedStatus,
        DateTime expiresAtUtc,
        DateTime nowUtc)
    {
        if (!MemberInviteStatuses.IsSupported(storedStatus))
        {
            return null;
        }

        return storedStatus == MemberInviteStatus.Pending && nowUtc >= expiresAtUtc
            ? MemberInviteStatus.Expired
            : storedStatus;
    }

    /// <summary>
    /// Whether an invite is still live at <paramref name="nowUtc"/> - pending, and not yet expired.
    /// It is the one condition under which an invite may be cancelled, and (under #532) redeemed.
    /// </summary>
    public static bool IsLive(MemberInviteStatus storedStatus, DateTime expiresAtUtc, DateTime nowUtc) =>
        EffectiveStatus(storedStatus, expiresAtUtc, nowUtc) == MemberInviteStatus.Pending;

    /// <summary>
    /// Reads a submitted note and yields the value to store: trimmed, or <see langword="null"/>
    /// when the caller sent nothing, an empty string or whitespace. "No note" has one stored
    /// representation, so a blank field and an absent field cannot become two different rows.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The note is longer than <see cref="MaxNoteLength"/> characters. Nothing is stored in that
    /// case, and the message does not echo the note.
    /// </exception>
    public static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var trimmed = note.Trim();

        return trimmed.Length <= MaxNoteLength
            ? trimmed
            : throw new DomainValidationException(NoteTooLongMessage);
    }
}
