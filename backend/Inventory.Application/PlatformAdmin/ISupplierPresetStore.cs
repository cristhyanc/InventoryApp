using Inventory.Domain.Suppliers;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// One platform supplier preset as the Application layer sees it (issue #848, with the
/// supplier-master fields of issue #931).
/// </summary>
/// <param name="Id">The row's own key.</param>
/// <param name="Name">The name as a Super Admin typed it, trimmed.</param>
/// <param name="Url">Its website, or <see langword="null"/> when none is recorded.</param>
/// <param name="IsEnabled">Whether it is still suggested.</param>
/// <param name="ServesNationwide">Whether it serves the whole country.</param>
/// <param name="ServedStates">
/// The states and territories it serves, in <c>SupplierPresetMasterPolicy.ServedStateCodes</c>'s own
/// order, and empty when <paramref name="ServesNationwide"/> is <see langword="true"/>. A list rather
/// than the stored string because that is what the resource publishes and what a caller submits; the
/// one canonical stored spelling is the policy's business.
/// </param>
/// <param name="PricesIncludeGst">Whether the prices it publishes include GST.</param>
/// <param name="PriceConnector">
/// How its prices are collected, or <see langword="null"/> when nothing collects them.
/// </param>
/// <param name="PriceConnectorKey">
/// What the connector needs in order to know which retailer it is for, or <see langword="null"/>
/// where the route takes none.
/// </param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed; equal to creation until the first edit.</param>
public sealed record SupplierPresetRecord(
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
    DateTime UpdatedAtUtc);

/// <summary>
/// A new preset, with every value already through <c>SupplierPresetPolicy</c> and
/// <c>SupplierPresetMasterPolicy</c>.
/// </summary>
/// <param name="Name">The validated, trimmed display name.</param>
/// <param name="NormalisedName">Its comparison key, which the unique index is over.</param>
/// <param name="Url">The validated website, or <see langword="null"/>.</param>
/// <param name="ServesNationwide">Whether it serves the whole country.</param>
/// <param name="ServedStates">
/// The canonical stored served-state value <c>SupplierPresetMasterPolicy.NormaliseServedStates</c>
/// produced - the declared codes in that policy's order, comma-separated, and the empty string for a
/// nationwide preset. The use case hands over the stored form, exactly as it hands over
/// <paramref name="NormalisedName"/>, so the adapter decides none of it.
/// </param>
/// <param name="PricesIncludeGst">Whether the prices it publishes include GST.</param>
/// <param name="PriceConnector">The validated collection route, or <see langword="null"/> for none.</param>
/// <param name="PriceConnectorKey">The validated connector key, or <see langword="null"/>.</param>
/// <param name="CreatedAtUtc">The instant to record as both created and updated.</param>
public sealed record NewSupplierPreset(
    string Name,
    string NormalisedName,
    string? Url,
    bool ServesNationwide,
    string ServedStates,
    bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey,
    DateTime CreatedAtUtc);

/// <summary>
/// An edit to one preset: every mutable field, always sent together, because the endpoint is a
/// <c>PUT</c> of the whole resource rather than one toggle per field.
/// </summary>
/// <param name="Name">The validated, trimmed display name.</param>
/// <param name="NormalisedName">Its comparison key.</param>
/// <param name="Url">The validated website, or <see langword="null"/> to clear it.</param>
/// <param name="IsEnabled">Whether it stays suggested.</param>
/// <param name="ServesNationwide">Whether it serves the whole country.</param>
/// <param name="ServedStates">
/// The canonical stored served-state value, on the terms <see cref="NewSupplierPreset"/> states.
/// </param>
/// <param name="PricesIncludeGst">Whether the prices it publishes include GST.</param>
/// <param name="PriceConnector">The validated collection route, or <see langword="null"/> for none.</param>
/// <param name="PriceConnectorKey">The validated connector key, or <see langword="null"/>.</param>
/// <param name="UpdatedAtUtc">The instant to record as last changed.</param>
public sealed record SupplierPresetEdit(
    string Name,
    string NormalisedName,
    string? Url,
    bool IsEnabled,
    bool ServesNationwide,
    string ServedStates,
    bool PricesIncludeGst,
    SupplierPriceConnector? PriceConnector,
    string? PriceConnectorKey,
    DateTime UpdatedAtUtc);

/// <summary>
/// The narrow persistence port over the platform supplier presets (issue #848), owned by the
/// Application layer and reached only from the Super Admin surface of that issue.
///
/// <para><strong>One table, and nothing else.</strong> Like <c>IPlatformBusinessDirectoryStore</c>
/// and <c>ISupportSessionStore</c>, no method here takes a predicate, a column list, a page, a sort
/// or a statement, and nothing tenant-owned is readable or writable through it: a preset is platform
/// configuration, and <c>Suppliers</c> is a business's own table. Copying a preset into a business is
/// issue #835 child C and is deliberately not a method here.</para>
///
/// <para><strong>There is no delete.</strong> A preset that should stop being suggested is disabled,
/// which is reversible and leaves the row - and anything a business already copied from it -
/// untouched.</para>
/// </summary>
public interface ISupplierPresetStore
{
    /// <summary>
    /// Every preset, enabled and disabled alike, ordered by name. It takes no filter at all: a
    /// caller-supplied predicate over a fixed platform list is how a list becomes a query engine,
    /// and hiding the disabled ones would make re-enabling one impossible.
    /// </summary>
    Task<IReadOnlyList<SupplierPresetRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether a preset other than <paramref name="excludingId"/> already holds
    /// <paramref name="normalisedName"/>.
    ///
    /// <para>This is the pre-check that turns the common case into a clear <c>409</c> instead of a
    /// database error. It is <em>not</em> the rule: between this read and the write another caller
    /// can insert the same name, which is why the unique index exists and why
    /// <see cref="IsDuplicateName"/> translates its violation.</para>
    /// </summary>
    Task<bool> ExistsWithNormalisedNameAsync(
        string normalisedName,
        int? excludingId,
        CancellationToken cancellationToken);

    /// <summary>Stores a new preset, enabled, and answers it as it was stored.</summary>
    Task<SupplierPresetRecord> AddAsync(NewSupplierPreset preset, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="edit"/> to the preset with <paramref name="id"/>, or answers
    /// <see langword="null"/> when there is no such preset.
    /// </summary>
    Task<SupplierPresetRecord?> UpdateAsync(
        int id,
        SupplierPresetEdit edit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the unique index on the normalised name refusing a
    /// duplicate, and nothing else.
    ///
    /// <para>It is asked of the adapter rather than decided in the use case because recognising a
    /// provider's constraint violation is provider knowledge: an EF or SQLite type must not reach
    /// <c>Inventory.Application</c>. Any other database failure is left untranslated rather than
    /// reported to a caller as a name conflict.</para>
    /// </summary>
    bool IsDuplicateName(Exception exception);
}
