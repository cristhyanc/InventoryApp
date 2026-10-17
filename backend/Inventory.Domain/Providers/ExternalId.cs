namespace Inventory.Domain.Providers;

/// <summary>
/// What an external identifier may be, independently of which provider issued it (issue #732).
///
/// <para>It is text and it is opaque: a link table stores it as written and compares it exactly.
/// Only provider-specific code - <see cref="NayaxExternalId"/> today - knows what the characters
/// mean, which is what lets a provider whose ids are not numbers be added without touching the
/// schema or any shared query.</para>
/// </summary>
public static class ExternalId
{
    /// <summary>
    /// The longest external identifier a link may store.
    ///
    /// <para>A bound rather than unlimited text, because the column is indexed and matched exactly:
    /// an identifier longer than this is not one this application failed to store, it is a value no
    /// provider issues. Nayax's are at most the 19 digits of an <c>int64</c>; the rest of the
    /// allowance is room for a provider that issues an opaque string or a GUID.</para>
    /// </summary>
    public const int MaxLength = 128;
}
