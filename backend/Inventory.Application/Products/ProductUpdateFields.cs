namespace Inventory.Application.Products;

/// <summary>
/// A product's editable fields, as seen by the Application layer.
///
/// <para><see cref="Name"/> and <see cref="CategoryId"/> joined them in issue #736 (parent #329
/// decision P2): a business without a sales provider maintains its own catalogue, so a product's
/// name and category have to be editable somewhere, and this is that place. They were deliberately
/// absent before, when every product arrived through the Nayax catalogue import and those fields
/// were the import's to write. The selling price that joined them at the same time is gone with the
/// field itself (issue #792, A2 of #786).</para>
///
/// <para><strong>A product the provider names still keeps its name and category.</strong> For a
/// product carrying an <c>ExternalProductId</c> the store refuses a change to either, with
/// <c>Inventory.Domain.Providers.ProviderManagedCatalogueFields</c>'s message, because the next
/// catalogue import would overwrite it; every other field here stays editable for every product.
/// Submitting the values the product already has is not a change and is accepted, which is what
/// lets a client send the whole product back.</para>
///
/// <para><strong>There is no selling price here since issue #792</strong> (A2 of #786): a product
/// carries none at all. What a product still sells for is the machine's own live price, and what it
/// costs is <c>AverageUnitCost</c> and the recorded purchase history - two different things that
/// were never this field (docs/architecture.md § A product carries no selling price).</para>
///
/// <para><strong>There is no supplier here since issue #787</strong> (A1 of #786): a product carries
/// no supplier metadata at all, so an edit has none to change. The supplier of a purchase and of a
/// supplier order is unaffected and is still chosen on those records.</para>
/// </summary>
public sealed record ProductUpdateFields(
    string? Name,
    string? Sku,
    string? Description,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
    bool IsActive);
