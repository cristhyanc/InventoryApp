namespace Inventory.Application.Categories;

/// <summary>
/// A persisted product category as seen by the Application layer.
///
/// <para><paramref name="IsProviderManaged"/> (issue #736) is whether the business's sales provider
/// names this category - that is, whether the row carries an <c>ExternalCategoryId</c>. It is the
/// fact and not the identifier, exactly as on a product: a provider's id is integration state, and
/// <c>Inventory.Domain.Providers.ProviderManagedCatalogueFields</c> turns this one bit into the
/// fields a client must lock and the refusal a local rename gets.</para>
/// </summary>
public sealed record CategoryRecord(long Id, string Name, string? Description, bool IsProviderManaged = false);
