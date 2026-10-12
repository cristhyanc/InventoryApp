namespace Inventory.Domain.Tenancy;

/// <summary>
/// The business's own contact email address as a submitted value is read and refused (issue #548).
///
/// It is a <em>format</em> rule and nothing more: it decides whether what somebody typed can be an
/// email address at all, so a typo is caught where it was made rather than discovered when a
/// message to the business bounces. It never proves the mailbox exists, is reachable, or belongs to
/// this business, and nothing may infer anything else from it - the address is business contact
/// detail, never an identity, for the same reason a member's own address is not one: an address is
/// mutable, is reassignable to a different party, and the thing that identifies anybody here is
/// the validated <see cref="ActorIdentity"/> claim pair.
///
/// <para>The rule is deliberately narrow rather than an attempt at RFC 5321: one <c>@</c> with a
/// non-empty local part, a domain carrying a dot with labels either side, no whitespace anywhere,
/// and a length a column and a user interface can both hold. A stricter grammar would reject
/// addresses that work, and a looser one would accept "owner@" - the mistake this check exists to
/// catch. Trimming the submitted value is the only normalisation: the case of an address is
/// meaningful to the local part, so it is stored exactly as the person typed it.</para>
/// </summary>
public static class BusinessContactEmail
{
    /// <summary>
    /// The longest address this application stores. 254 is the maximum length an email address can
    /// have in an SMTP envelope, so a longer value cannot be a deliverable address.
    /// </summary>
    public const int MaxLength = 254;

    /// <summary>
    /// The message an unusable address is refused with. It names the rule rather than the value, is
    /// written for the person who typed it, and carries no internal detail.
    /// </summary>
    public const string InvalidMessage =
        "A contact email address must be a single address of the form name@example.com.";

    /// <summary>
    /// Reads a submitted address and yields the value to store: the same address with surrounding
    /// whitespace removed. A value that cannot be an address is refused rather than repaired.
    /// </summary>
    public static bool TryNormalize(string? submitted, out string? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        var candidate = submitted.Trim();

        if (candidate.Length > MaxLength || candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = candidate.Split('@');
        if (parts.Length != 2)
        {
            return false;
        }

        var (local, domain) = (parts[0], parts[1]);
        if (local.Length == 0 || !HasDottedLabels(domain))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// Whether the domain is at least two non-empty labels separated by dots - <c>example.com</c>
    /// and not <c>example</c>, <c>.com</c> or <c>example..com</c>. A single-label domain is
    /// technically addressable on a local network and is never what somebody means when recording
    /// a business's contact address.
    /// </summary>
    private static bool HasDottedLabels(string domain)
    {
        var labels = domain.Split('.');

        return labels.Length >= 2 && labels.All(label => label.Length > 0);
    }
}
