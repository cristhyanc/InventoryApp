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
/// <para>The rule is deliberately narrow rather than an attempt at RFC 5321: one <c>@</c>, no
/// whitespace anywhere, a length a column and a user interface can both hold, a mailbox of
/// dot-separated parts made of the unquoted characters an address is ordinarily written with, and a
/// domain that could be a host name - at least two labels, each one letters, digits and interior
/// hyphens, ending in an alphabetic top-level one. A stricter grammar would reject addresses that
/// work, and a looser one would accept "owner@" or, as issue #593 found, "owner@!.com" and
/// "owner@example.com&gt;" - the mistakes this check exists to catch. Trimming the submitted value
/// is the only normalisation: the case of an address is meaningful to the local part, so it is
/// stored exactly as the person typed it.</para>
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
    /// The characters a mailbox may be written with beside letters and digits: RFC 5322's unquoted
    /// set, which is what an address looks like on a business card. The ones it leaves out are the
    /// point - <c>&lt;</c>, <c>&gt;</c>, <c>(</c>, <c>)</c>, <c>[</c>, <c>]</c>, <c>:</c>,
    /// <c>;</c>, <c>,</c>, <c>"</c> and <c>\</c> are how a mail client delimits an address, a
    /// display name, a comment or a second recipient, so one arriving in this field is a paste that
    /// brought its surroundings with it rather than an address.
    /// </summary>
    private const string MailboxSymbols = "!#$%&'*+-/=?^_`{|}~";

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
        if (!IsMailbox(local) || !IsHostName(domain))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// Whether the local part is dot-separated parts of letters, digits and
    /// <see cref="MailboxSymbols"/>, each part non-empty -
    /// <c>owner</c>, <c>owner.name</c> and <c>owner+vending</c>, and not <c>&lt;owner</c>,
    /// <c>owner.</c> or <c>owner..name</c>. A quoted local part (<c>"owner name"@example.com</c>)
    /// is legal in RFC 5322 and refused here deliberately: nobody records a business's contact
    /// address that way, and accepting quotes is what lets a pasted <c>"Owner" &lt;...&gt;</c>
    /// through.
    /// </summary>
    private static bool IsMailbox(string local) =>
        local.Length > 0
        && local.Split('.').All(part =>
            part.Length > 0
            && part.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || MailboxSymbols.Contains(character, StringComparison.Ordinal)));

    /// <summary>
    /// Whether the domain could be a host name: at least two labels separated by dots, each one
    /// non-empty and made of ASCII letters, digits and interior hyphens, with an alphabetic
    /// top-level label of at least two characters.
    ///
    /// <para>So <c>example.com</c> and <c>ex-ample.co.uk</c>, and not <c>example</c>, <c>.com</c>,
    /// <c>example..com</c>, <c>!.com</c>, <c>exa_mple.com</c>, <c>-example.com</c> or
    /// <c>example.c0m</c>. A single-label domain is technically addressable on a local network and
    /// is never what somebody means when recording a business's contact address; a label holding a
    /// character a host name cannot is a typo or a paste, which is the mistake issue #593 reported.
    /// An internationalised domain must be submitted in its ASCII-compatible form, and a bracketed
    /// IP literal is refused - neither is how a business publishes its own address.</para>
    /// </summary>
    private static bool IsHostName(string domain)
    {
        var labels = domain.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            if (!label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return false;
            }
        }

        var topLevel = labels[^1];

        return topLevel.Length >= 2 && topLevel.All(char.IsAsciiLetter);
    }
}
