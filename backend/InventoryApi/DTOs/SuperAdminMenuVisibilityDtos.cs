using System.Text.Json.Serialization;

using Inventory.Application.PlatformOptions;

namespace InventoryApi.DTOs;

/// <summary>
/// The whole switchable menu catalogue and the state each destination is in, as
/// <c>GET /api/super-admin/menu-visibility</c> reports it (issue #1148).
///
/// <para>Every catalogue key is always present, whether its state was stored or defaulted: the Super
/// Admin page shows a complete list of what can be switched, and "no row yet" is not a state anybody
/// needs to see. It carries no business, member or actor identifier, because the setting is about
/// none of them.</para>
/// </summary>
/// <param name="Options">Every switchable destination, in the catalogue's own order.</param>
public sealed record SuperAdminMenuVisibilityResponse(
    IReadOnlyList<SuperAdminMenuVisibilityOptionResponse> Options)
{
    public static SuperAdminMenuVisibilityResponse From(MenuVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);

        return new SuperAdminMenuVisibilityResponse(
            [.. visibility.Options.Select(SuperAdminMenuVisibilityOptionResponse.From)]);
    }
}

/// <summary>
/// One switchable destination as the Super Admin surface reports it (issue #1148), and the body
/// <c>PUT /api/super-admin/menu-visibility/{key}</c> answers with.
/// </summary>
/// <param name="Key">
/// The destination's server-owned catalogue key - <c>admin.imports</c> and the six beside it. A
/// client may send one back on the <c>PUT</c> and may never invent one: an unknown key is answered
/// <c>404</c> with nothing stored.
/// </param>
/// <param name="Label">
/// The destination's display label, the same words the Admin menu uses, so the page needs no second
/// copy of that wording.
/// </param>
/// <param name="Visible">
/// Whether the destination is offered to members who are not the platform administrator, as it is
/// now stored or defaulted. A Super Admin keeps every destination in both states.
/// </param>
public sealed record SuperAdminMenuVisibilityOptionResponse(string Key, string Label, bool Visible)
{
    public static SuperAdminMenuVisibilityOptionResponse From(MenuVisibilityOptionState option)
    {
        ArgumentNullException.ThrowIfNull(option);

        return new SuperAdminMenuVisibilityOptionResponse(option.Key, option.Label, option.Visible);
    }
}

/// <summary>
/// What a Super Admin submits to <c>PUT /api/super-admin/menu-visibility/{key}</c> (issue #1148):
/// the state that one destination is to end in.
///
/// <para>Which destination it is about is the <em>route</em>, so the body carries no key, and it
/// carries no business, member or actor either: the setting is one shared application-wide answer
/// per destination.</para>
/// </summary>
/// <param name="Visible">
/// Whether the destination is to be offered to members who are not the platform administrator.
/// Marked <see cref="JsonRequiredAttribute"/> so a body that omits it is refused with <c>400</c>
/// rather than silently switching the destination off - the field is a <c>bool</c>, so a missing
/// value and an explicit <c>false</c> are otherwise indistinguishable.
/// </param>
public sealed record SuperAdminMenuVisibilityRequest([property: JsonRequired] bool Visible);
