using Inventory.Application.PlatformAdmin;

namespace InventoryApi.DTOs;

/// <summary>
/// One platform supplier preset as <c>GET /api/super-admin/supplier-presets</c> reports it, and as
/// the create and edit calls answer (issue #848).
///
/// <para>These six fields are the whole contract. The stored normalised name is deliberately absent:
/// it is the unique index's key, not a field of the resource, and publishing it would invite a client
/// to decide for itself whether two names collide - a rule that belongs to the database.</para>
///
/// <para>Nothing here is tenant data: a preset belongs to no business, and a business's own
/// suppliers are reported by <c>/api/suppliers</c>, which knows nothing about this table.</para>
/// </summary>
/// <param name="Id">The preset's own key, which the edit call names.</param>
/// <param name="Name">Its name, as a Super Admin typed it.</param>
/// <param name="Url">Its website, or <c>null</c> when none is recorded.</param>
/// <param name="IsEnabled">Whether it is still suggested. Disabled presets are reported too.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed; equal to creation until the first edit.</param>
public sealed record SuperAdminSupplierPresetResponse(
    int Id,
    string Name,
    string? Url,
    bool IsEnabled,
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
            preset.CreatedAtUtc,
            preset.UpdatedAtUtc);
    }
}

/// <summary>
/// The body of <c>POST /api/super-admin/supplier-presets</c> (issue #848).
///
/// <para>A new preset is created enabled, so the request carries no <c>isEnabled</c>: "create it
/// already switched off" is not a thing anybody asked for, and disabling is one edit away. Both
/// values are validated by <c>SupplierPresetPolicy</c> on the server; nothing here is trusted.</para>
/// </summary>
/// <param name="Name">The preset's name. Required, trimmed, at most 200 characters.</param>
/// <param name="Url">
/// Its website, or <c>null</c>/blank for none. An absolute http(s) address of at most 2048
/// characters, under the shared supplier URL rule.
/// </param>
public sealed record SuperAdminCreateSupplierPresetRequest(string? Name, string? Url);

/// <summary>
/// The body of <c>PUT /api/super-admin/supplier-presets/{id}</c> (issue #848): the whole resource,
/// so enabling and disabling is this call rather than a separate endpoint over the same row.
/// </summary>
/// <param name="Name">The preset's name. Required, trimmed, at most 200 characters.</param>
/// <param name="Url">Its website, or <c>null</c>/blank to record none.</param>
/// <param name="IsEnabled">Whether it stays suggested.</param>
public sealed record SuperAdminUpdateSupplierPresetRequest(string? Name, string? Url, bool IsEnabled);
