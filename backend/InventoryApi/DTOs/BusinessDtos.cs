namespace InventoryApi.DTOs;

/// <summary>
/// The signed-in operator's own business as <c>GET /api/business/current</c> returns it (issue
/// #499): what it is called, and the IANA time zone the frontend must render instants and resolve
/// calendar inputs in.
///
/// It deliberately carries no business identifier: the frontend has no use for one, and a tenant
/// key in a response is a key a later request could try to send back.
/// </summary>
public sealed record CurrentBusinessResponse(string Name, string TimeZoneId);

/// <summary>
/// The business's own settings as <c>GET</c>/<c>PUT /api/business/settings</c> returns them (issue
/// #548).
///
/// Four of the six are nullable because "not recorded" is a real state for a business that
/// predates the application form (issue #540, and issue #933 for the state): the read reports that
/// rather than inventing a value. A <em>save</em> is narrower - a contact address, a GST
/// registration and a state are required, and only the ABN may be cleared - so what this record
/// describes becomes steadily more complete.
///
/// <c>State</c> is the Australian state or territory the business trades in, as one of
/// <c>Inventory.Domain.Tenancy.AustralianStates.All</c>, and <c>null</c> when none has been
/// recorded (issue #933). A business with none is what the Business Settings page's legacy-profile
/// prompt asks the Owner to complete; nothing else is blocked while it is unset.
///
/// Like <see cref="CurrentBusinessResponse"/> it carries no business identifier: the business is
/// the caller's own, resolved from their membership, and a tenant key in a response is a key a
/// later request could try to send back.
/// </summary>
public sealed record BusinessSettingsResponse(
    string Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string TimeZoneId,
    string? State);

/// <summary>
/// The settings body <c>PUT /api/business/settings</c> accepts (issue #548). Every setting is
/// submitted together: the save replaces the whole set rather than patching one field.
///
/// Every member is nullable so that "not sent" is distinguishable from "sent empty", and both are
/// refused with <c>400</c> rather than silently defaulted - a missing <see cref="GstRegistered"/>
/// bound to <see langword="false"/> would record a tax fact nobody stated. There is deliberately no
/// business identifier and no status, role or membership field: the business comes from the
/// caller's membership, and its lifecycle is not an Owner's to set here.
///
/// <c>State</c> is required (issue #933) and must be one of <c>NSW</c>, <c>VIC</c>, <c>QLD</c>,
/// <c>SA</c>, <c>WA</c>, <c>TAS</c>, <c>ACT</c> or <c>NT</c>; anything else is a <c>400</c>, and it
/// is never inferred from <see cref="TimeZoneId"/>.
/// </summary>
public sealed record BusinessSettingsRequest(
    string? Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string? TimeZoneId,
    string? State);
