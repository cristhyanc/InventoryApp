using System.Text.Json.Serialization;

using Inventory.Application.PlatformOptions;

namespace InventoryApi.DTOs;

/// <summary>
/// What a Super Admin submits to <c>PUT /api/super-admin/option-visibility</c> (issue #873): the
/// state the one switchable option is to end in.
///
/// <para>It carries no business, member, actor or option key: the setting is one shared
/// application-wide answer, and which option it is about is the endpoint rather than a value a
/// caller chooses.</para>
/// </summary>
/// <param name="HistoricalGstClassificationVisible">
/// Whether Historical GST Classification is offered to members who are not the platform
/// administrator. Marked <see cref="JsonRequiredAttribute"/> so a body that omits it is refused
/// with <c>400</c> rather than silently switching the option off - the field is a <c>bool</c>, so a
/// missing value and an explicit <c>false</c> are otherwise indistinguishable.
/// </param>
public sealed record SuperAdminOptionVisibilityRequest(
    [property: JsonRequired] bool HistoricalGstClassificationVisible);

/// <summary>
/// The shared option visibility as the API reports it (issue #873), by the Super Admin surface that
/// wrote it. <c>GET /api/me/access</c> reports the same boolean to a member as part of its own
/// response; see <see cref="MeAccessResponse"/>.
/// </summary>
/// <param name="HistoricalGstClassificationVisible">
/// Whether Historical GST Classification is offered to members who are not the platform
/// administrator, as it is now stored.
/// </param>
public sealed record SuperAdminOptionVisibilityResponse(bool HistoricalGstClassificationVisible)
{
    public static SuperAdminOptionVisibilityResponse From(OptionVisibilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new SuperAdminOptionVisibilityResponse(settings.HistoricalGstClassificationVisible);
    }
}
