using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Legal;

/// <summary>
/// The published legal documents' current version, and the one rule about accepting it (issue
/// #1259).
///
/// <para><strong>This is the single definition of the version, and it lives here on purpose.</strong>
/// The Terms of Use say "By accepting these terms when registering or joining a business, you agree
/// to them", so the two endpoints that register and join have to require an acceptance and record
/// which version was accepted. A version each side of the wire held its own copy of would drift the
/// first time one of them was published without the other, so the backend owns it, the frontend
/// reads it from <c>GET /api/me/account-state</c>, and the published pages' effective date is
/// checked against this constant by
/// <c>frontend/inventory-app/src/app/architecture/legal-version-contract.guard.spec.ts</c>.</para>
///
/// <para>Like every other type in this project it holds no clock, no database and no configuration:
/// the version is a published fact about the documents rather than a deployment setting, and a
/// deployment that could configure its own Terms version could record an agreement to something
/// nobody published.</para>
/// </summary>
public static class LegalTerms
{
    /// <summary>
    /// The version of the Terms of Use and the Privacy Policy that is in force, as the ISO-8601
    /// calendar date they took effect on.
    ///
    /// <para>A date rather than a number because that is what the documents themselves publish -
    /// "Effective date: 13 November 2026" - so the stored value can be read against the document it
    /// refers to years later without a lookup table. It is the exact string a request must carry:
    /// nothing parses, normalises or reformats it, because a comparison that accepted several
    /// spellings of one date would be a comparison the two sides could disagree about.</para>
    /// </summary>
    public const string CurrentVersion = "2026-11-13";

    /// <summary>
    /// The refusal for a request that carries no accepted version, or one that is not
    /// <see cref="CurrentVersion"/>.
    ///
    /// <para>One sentence for both, and it says what to do rather than what went wrong. The window
    /// it exists for is the few minutes between the API deploy and the frontend deploy, plus any
    /// browser tab that has been open across one: that page offered the previous documents, so the
    /// agreement it would record is not the agreement the person gave. Reloading is the whole
    /// remedy, and it is the only thing the sentence asks for - it names no version, because which
    /// version a stale page held is not something the person who has to refresh can act on.</para>
    /// </summary>
    public const string StaleVersionMessage =
        "Our Terms of Use have been updated. Please refresh the page and try again.";

    /// <summary>
    /// Whether <paramref name="acceptedVersion"/> is the version in force. An absent, blank or
    /// different value is not, and a value carrying surrounding whitespace is not either: the
    /// request is machine-generated from what the page was served, so there is nothing to be
    /// forgiving about.
    /// </summary>
    public static bool IsCurrent(string? acceptedVersion) =>
        string.Equals(acceptedVersion, CurrentVersion, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="CurrentVersion"/> when <paramref name="acceptedVersion"/> is it, and a refusal
    /// otherwise. Returning the constant rather than the submitted string is deliberate: what is
    /// stored is then the version this build published, not a value that came in over the wire and
    /// merely compared equal to it.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The request carried no version, or a version that is not the current one. The caller writes
    /// nothing.
    /// </exception>
    public static string RequireCurrent(string? acceptedVersion) =>
        IsCurrent(acceptedVersion)
            ? CurrentVersion
            : throw new DomainValidationException(StaleVersionMessage);
}
