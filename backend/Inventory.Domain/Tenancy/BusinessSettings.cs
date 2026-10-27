namespace Inventory.Domain.Tenancy;

/// <summary>
/// The settings a business keeps about itself (issue #548): what it is called, how it is contacted,
/// its ABN, whether it is registered for GST, the IANA time zone its business days are derived in,
/// and the Australian state or territory it trades in (issue #933).
///
/// <para>It is the stored shape, which is why four of the six are nullable: the application's
/// existing business predates the application form, so "not recorded" is a real state for its
/// contact address, its ABN, its GST registration and its state, and a read has to be able to
/// report that rather than invent a value (issue #540, and issue #933 for the state). A
/// <em>save</em> is narrower - a contact address, a GST registration and a state are required on
/// the way in, and <see cref="BusinessSettingsPolicy.Validate"/> is where that is decided - so a
/// record this type describes becomes steadily more complete and never less.</para>
///
/// <para>It deliberately carries no business identifier. Which business these settings belong to is
/// resolved from the authenticated actor's membership, and a tenant key in a contract is a key a
/// later request could try to send back.</para>
/// </summary>
/// <param name="Name">The operator-facing display name; never an identifier.</param>
/// <param name="ContactEmail">
/// The business's own contact address, or <see langword="null"/> when none has been recorded.
/// </param>
/// <param name="Abn">
/// The Australian Business Number as 11 digits with no spaces, or <see langword="null"/> when none
/// has been recorded. It is the one optional setting a save may clear.
/// </param>
/// <param name="GstRegistered">
/// Whether the business is registered for GST, or <see langword="null"/> when that has not been
/// recorded. Three states, deliberately: registration is never inferred from an
/// <paramref name="Abn"/>, which an unregistered entity may legitimately hold.
/// </param>
/// <param name="TimeZoneId">
/// The IANA time-zone id the business's calendar days are derived in, for example
/// <c>Australia/Sydney</c>. Required, because a business with no zone has no derivable business
/// dates at all.
/// </param>
/// <param name="State">
/// The Australian state or territory the business trades in, as one of
/// <see cref="AustralianStates.All"/>, or <see langword="null"/> when none has been recorded
/// (issue #933). It is never derived from <paramref name="TimeZoneId"/>, which cannot tell New
/// South Wales from the Australian Capital Territory, and never from an address.
/// </param>
public sealed record BusinessSettings(
    string Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string TimeZoneId,
    string? State);

/// <summary>
/// One setting, as an audit event names it (issue #548).
///
/// It exists so that "which fields changed" is a closed vocabulary rather than a list of strings
/// assembled at a call site: an audit trail is evidence, and evidence whose spelling drifts between
/// two code paths is harder to read than one that cannot. Declaration order is the order a change
/// set is reported in, so the same change always reads the same way.
///
/// The vocabulary names the <em>fields</em> and never carries a value, which is the point: the
/// contact address and the ABN are personal and tax detail about the business, and the audit trail
/// records that they changed without recording what they changed to.
/// </summary>
public enum BusinessSettingsField
{
    /// <summary>The operator-facing display name.</summary>
    Name = 1,

    /// <summary>The business's own contact email address.</summary>
    ContactEmail = 2,

    /// <summary>The Australian Business Number.</summary>
    Abn = 3,

    /// <summary>Whether the business is registered for GST.</summary>
    GstRegistered = 4,

    /// <summary>The IANA time zone business days are derived in.</summary>
    TimeZoneId = 5,

    /// <summary>The Australian state or territory the business trades in (issue #933).</summary>
    State = 6,
}
