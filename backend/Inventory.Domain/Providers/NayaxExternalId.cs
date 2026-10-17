using System.Globalization;

namespace Inventory.Domain.Providers;

/// <summary>
/// The one conversion between a Nayax numeric identifier and the text an
/// <see cref="ExternalProvider.Nayax"/> link stores in its <c>ExternalId</c> column (issue #732,
/// parent #329 decision P1).
///
/// <para><strong>An <c>ExternalId</c> is opaque text.</strong> The link tables compare it exactly
/// and never interpret it, so shared code - the tenancy filters, the unique indexes, the backfill,
/// a later provider - needs no idea of what a given provider's ids look like. Nayax's happen to be
/// numbers: the Lynx contract declares <c>NayaxProductID</c> as <c>integer&lt;int64&gt;</c> and
/// <c>ProductGroupID</c> as <c>integer&lt;int32&gt;</c> (Nayax developer portal, <c>GET
/// /v1/operators/{OperatorID}/products</c> and <c>GET /v1/operators/{ActorID}/productGroups</c>),
/// so both fit a <see cref="long"/>. Turning one into text, and reading it back, is the one place
/// that fact is written down.</para>
///
/// <para><strong>Invariant decimal, because the stored value is matched byte for byte.</strong> A
/// culture-sensitive format could write a group separator or a non-ASCII digit, and the resulting
/// row would simply never match the id it was written for. The same shape is what the backfill
/// migration writes with SQLite's <c>CAST(Id AS TEXT)</c>, which produces exactly this
/// representation for an integer.</para>
///
/// <para>It holds no clock, no database and no configuration: every answer is a function of its
/// argument, so the import, a later lookup (#733, #745, #747, #734) and a test all convert
/// identically.</para>
/// </summary>
public static class NayaxExternalId
{
    /// <summary>
    /// The <c>ExternalId</c> text for a Nayax numeric identifier: its invariant decimal form.
    /// </summary>
    public static string From(long nayaxId) => nayaxId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a stored <c>ExternalId</c> back as the Nayax numeric identifier it was written from,
    /// returning <see langword="false"/> for any text this helper could not have produced.
    ///
    /// <para>Deliberately strict: only an optional sign and decimal digits are accepted, with no
    /// whitespace, no group separator, no hexadecimal and no exponent. A link whose text is not a
    /// Nayax id must fail to resolve rather than resolve to a nearby number, because the caller is
    /// about to use the answer as the identity of a product.</para>
    /// </summary>
    public static bool TryParse(string? externalId, out long nayaxId)
    {
        if (externalId is null)
        {
            nayaxId = 0;
            return false;
        }

        return long.TryParse(externalId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nayaxId);
    }
}
