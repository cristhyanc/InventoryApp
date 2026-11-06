namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What a Super Admin may record as a business's <strong>Nayax SQS sender identity</strong> (issue
/// #1101, child of #1100): the AWS IAM user id whose messages on the shared Restockroo queue belong
/// to that business, and nothing else about it.
///
/// <para>It is a pure set of rules: no clock, no database, no actor and no revision. The use case
/// that calls them supplies the instant and compares the revision, which is what keeps every
/// boundary below testable without a request.</para>
///
/// <para><strong>The value is an identifier, never a secret.</strong> SQS reports it as the
/// <c>SenderId</c> system attribute of a received message, and an administrator reads it with
/// <c>aws iam get-user --user-name &lt;user&gt; --query User.UserId</c>. An AWS <em>access key</em>
/// or secret is not this value and must never be entered, stored or logged anywhere in this
/// application (AGENTS.md § Security).</para>
///
/// <para><strong>What it deliberately does not decide.</strong> Which business a received message
/// belongs to - that is the queue reader's lookup (issue #1103), which is not built yet and is no
/// part of this surface - and whether the business may be written at all, which is
/// <see cref="Tenancy.BusinessStatus"/>'s answer read by the use case.</para>
/// </summary>
public static class NayaxSqsSenderPolicy
{
    /// <summary>
    /// The shortest sender id that may be recorded. An AWS IAM user id is a prefixed unique id
    /// (<c>AIDA…</c>), so anything shorter is a typed fragment rather than an identity.
    /// </summary>
    public const int MinSenderIdLength = 16;

    /// <summary>The longest one, matching the stored column's length.</summary>
    public const int MaxSenderIdLength = 128;

    /// <summary>
    /// The refusal for a sender id that is not 16 to 128 letters and digits. It names the rule and
    /// never the value, so a refusal cannot echo somebody's paste back into a log or a page.
    /// </summary>
    public static readonly string InvalidSenderIdMessage =
        $"An AWS sender ID must be {MinSenderIdLength} to {MaxSenderIdLength} characters long and "
            + "contain only letters and digits.";

    /// <summary>
    /// The refusal for switching the mapping on while it records no sender id. An enabled row with
    /// nothing to match on would claim the queue is attributed for this business when it is not.
    /// </summary>
    public const string EnabledNeedsSenderIdMessage =
        "A Nayax SQS sender can only be turned on once its AWS sender ID has been recorded.";

    /// <summary>
    /// The refusal for a write that presents a revision other than the stored one: another
    /// administrator - or this one's other tab - has changed the mapping since it was read.
    /// </summary>
    public const string StaleRevisionMessage =
        "This business's Nayax SQS sender has changed since it was read. Reload it and try again.";

    /// <summary>
    /// The refusal for a sender id another business already holds.
    ///
    /// <para>It names no business and hints at none, deliberately: which business holds an identity
    /// is not a fact this surface may publish, and the administrator who recorded it elsewhere can
    /// find it from the sender's own business. The condition is detected by the global unique index,
    /// so it never depends on reading another business's row.</para>
    /// </summary>
    public const string DuplicateSenderIdMessage =
        "That AWS sender ID is already recorded for another business, so it cannot be recorded here.";

    /// <summary>
    /// The refusal for a business that is not approved and trading. A sender identity decides which
    /// business the shared queue's sales are saved into, and a business that resolves nothing has no
    /// sales to save.
    /// </summary>
    public const string BusinessNotActiveMessage =
        "This business is not active, so its Nayax SQS sender cannot be changed.";

    /// <summary>
    /// The sender id as it is stored: trimmed, and <see langword="null"/> when there is nothing left -
    /// so whitespace is no sender rather than a blank one, and "remove" and "a field somebody
    /// cleared" are the same single representation.
    /// </summary>
    public static string? NormaliseSenderId(string? senderId)
    {
        var trimmed = senderId?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="senderId"/> is a value this application will record: 16 to 128
    /// characters, ASCII letters and digits only.
    ///
    /// <para>ASCII rather than <see cref="char.IsLetterOrDigit(char)"/> on purpose. An AWS unique id
    /// is <c>[A-Z0-9]</c>, and the Unicode predicate would accept a Devanagari digit or a Cyrillic
    /// letter that looks like a Latin one - which is how two visually identical ids could be
    /// recorded for two businesses and the global unique index below would be right to allow both.
    /// Case is kept as entered, because the value is stored as entered and matched exactly.</para>
    /// </summary>
    public static bool IsWellFormedSenderId(string? senderId) =>
        senderId is not null
            && senderId.Length >= MinSenderIdLength
            && senderId.Length <= MaxSenderIdLength
            && senderId.All(char.IsAsciiLetterOrDigit);
}
