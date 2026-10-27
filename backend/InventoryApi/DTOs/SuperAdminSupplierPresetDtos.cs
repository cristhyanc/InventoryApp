using System.Text.Json.Serialization;

using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Suppliers;

namespace InventoryApi.DTOs;

/// <summary>
/// One platform supplier preset as <c>GET /api/super-admin/supplier-presets</c> reports it, and as
/// the create and edit calls answer (issues #848 and #931).
///
/// <para>These eleven fields are the whole contract. The stored normalised name is deliberately
/// absent: it is the unique index's key, not a field of the resource, and publishing it would invite
/// a client to decide for itself whether two names collide - a rule that belongs to the database.</para>
///
/// <para><strong>The supplier-master fields are published in the shape each of them actually is.</strong>
/// The served states are the declared codes themselves, because "NSW" is the state's own name and
/// publishing a number for it would make the client hold a translation table that could drift. The
/// connector is its enum's number, as every other enum this API publishes is (a product's GST rule, a
/// business's sales provider), because its members name collection routes rather than values a person
/// reads. Both vocabularies are closed, and a value outside either one is refused with <c>400</c>
/// rather than stored.</para>
///
/// <para>Nothing here is tenant data: a preset belongs to no business, and a business's own
/// suppliers are reported by <c>/api/suppliers</c>, which knows neither this table nor any of these
/// fields - issue #849's copy carries the name and the website and nothing else.</para>
/// </summary>
/// <param name="Id">The preset's own key, which the edit call names.</param>
/// <param name="Name">Its name, as a Super Admin typed it.</param>
/// <param name="Url">
/// Its website, or <c>null</c> when none is recorded. For a JB Metro record this is also the site its
/// price connector reads.
/// </param>
/// <param name="IsEnabled">Whether it is still suggested. Disabled presets are reported too.</param>
/// <param name="ServesNationwide">Whether it serves the whole country.</param>
/// <param name="ServedStates">
/// The states and territories it serves, as the declared codes in the server's own order. Empty when
/// <paramref name="ServesNationwide"/> is <c>true</c> - and also empty for a preset nobody has
/// recorded a market for yet, which is a preset an edit must complete.
/// </param>
/// <param name="PricesIncludeGst">Whether the prices it publishes include GST.</param>
/// <param name="PriceConnector">
/// How its prices are collected, or <c>null</c> when nothing collects them.
/// </param>
/// <param name="PriceConnectorKey">
/// Which retailer the connector is for, or <c>null</c> where the route takes no key.
/// </param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed; equal to creation until the first edit.</param>
public sealed record SuperAdminSupplierPresetResponse(
    int Id,
    string Name,
    string? Url,
    bool IsEnabled,
    bool ServesNationwide,
    IReadOnlyList<string> ServedStates,
    bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public static SuperAdminSupplierPresetResponse From(SupplierPresetRecord preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return new SuperAdminSupplierPresetResponse(
            preset.Id,
            preset.Name,
            preset.Url,
            preset.IsEnabled,
            preset.ServesNationwide,
            preset.ServedStates,
            preset.PricesIncludeGst,
            preset.PriceConnector,
            preset.PriceConnectorKey,
            preset.CreatedAtUtc,
            preset.UpdatedAtUtc);
    }
}

/// <summary>
/// The body of <c>POST /api/super-admin/supplier-presets</c> (issues #848 and #931).
///
/// <para>A new preset is created enabled, so the request carries no <c>isEnabled</c>: "create it
/// already switched off" is not a thing anybody asked for, and disabling is one edit away. Every
/// value is validated on the server by <c>SupplierPresetPolicy</c> and
/// <c>SupplierPresetMasterPolicy</c>; nothing here is trusted.</para>
/// </summary>
/// <param name="Name">The preset's name. Required, trimmed, at most 200 characters.</param>
/// <param name="Url">
/// Its website, or <c>null</c>/blank for none. An absolute http(s) address of at most 2048
/// characters, under the shared supplier URL rule.
/// </param>
/// <param name="ServesNationwide">
/// Whether it serves the whole country. Mutually exclusive with <paramref name="ServedStates"/>, and
/// one of the two is required. It is <em>not</em> marked required on the wire, because omitting it is
/// never silently wrong: a body with states binds it to <c>false</c>, which is what those states
/// mean, and a body with neither is refused with <c>400</c>.
/// </param>
/// <param name="ServedStates">
/// The states and territories it serves, as the declared codes in any order, case or padding.
/// Omitted or empty for a nationwide preset.
/// </param>
/// <param name="PricesIncludeGst">
/// Whether the prices it publishes include GST. Marked <see cref="JsonRequiredAttribute"/> for the
/// reason <c>isEnabled</c> is on the edit, and more sharply: the field is a <c>bool</c>, so a body
/// that omitted it would bind <c>false</c> indistinguishably from an explicit <c>false</c> and record
/// the supplier's price list as ex-GST - a wrong tax basis, stored silently. A missing value is
/// refused instead.
/// </param>
/// <param name="PriceConnector">
/// How its prices are collected, or <c>null</c> for none. A number outside the declared
/// <c>SupplierPriceConnector</c> vocabulary is refused with <c>400</c>, because a JSON number binds
/// an arbitrary integer and such a value is a route nothing can take rather than "no route".
/// </param>
/// <param name="PriceConnectorKey">
/// Which retailer the connector is for. Required for the Varnmill route and refused for every other
/// route and for no route at all.
/// </param>
public sealed record SuperAdminCreateSupplierPresetRequest(
    string? Name,
    string? Url,
    bool ServesNationwide,
    IReadOnlyList<string?>? ServedStates,
    [property: JsonRequired] bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey)
{
    /// <summary>
    /// The submission the use cases take, built from this body. Only the transport shape differs
    /// between the create and the edit - the edit also carries <c>isEnabled</c> - so both map onto
    /// one Application type and neither re-states a rule.
    /// </summary>
    public SupplierPresetSubmission ToSubmission() =>
        new(Name, Url, ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, PriceConnectorKey);
}

/// <summary>
/// The body of <c>PUT /api/super-admin/supplier-presets/{id}</c> (issues #848 and #931): the whole
/// resource, so enabling and disabling is this call rather than a separate endpoint over the same row.
///
/// <para>Every field arriving together is what makes the supplier-master rules decidable: whether
/// nationwide conflicts with a state list, and whether the connector key belongs to the connector,
/// are questions about a whole submission rather than about one field.</para>
/// </summary>
/// <param name="Name">The preset's name. Required, trimmed, at most 200 characters.</param>
/// <param name="Url">Its website, or <c>null</c>/blank to record none.</param>
/// <param name="IsEnabled">
/// Whether it stays suggested. Marked <see cref="JsonRequiredAttribute"/> so a body that omits it
/// is refused with <c>400</c> rather than silently disabling the preset - the field is a
/// <c>bool</c>, so a missing value and <c>false</c> are otherwise indistinguishable.
/// </param>
/// <param name="ServesNationwide">Whether it serves the whole country, on the create's terms.</param>
/// <param name="ServedStates">The states and territories it serves, on the create's terms.</param>
/// <param name="PricesIncludeGst">
/// Whether the prices it publishes include GST. Required on the wire for the same reason as on the
/// create: an omitted value would silently re-record the price list as ex-GST.
/// </param>
/// <param name="PriceConnector">How its prices are collected, or <c>null</c> for none.</param>
/// <param name="PriceConnectorKey">Which retailer the connector is for.</param>
public sealed record SuperAdminUpdateSupplierPresetRequest(
    string? Name,
    string? Url,
    [property: JsonRequired] bool IsEnabled,
    bool ServesNationwide,
    IReadOnlyList<string?>? ServedStates,
    [property: JsonRequired] bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey)
{
    /// <inheritdoc cref="SuperAdminCreateSupplierPresetRequest.ToSubmission" />
    public SupplierPresetSubmission ToSubmission() =>
        new(Name, Url, ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, PriceConnectorKey);
}
