using System.Text.Json.Serialization;
using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One provider product or product group inside a <see cref="ProviderCatalogueReview"/> (issue #738).
///
/// <para>It is the provider's side of the review, recorded as read: the provider's id, its name, the
/// group it belongs to and the negotiable values it reported, each as the opaque text
/// <c>Inventory.Domain.Providers.ProviderCatalogueFieldValue</c> renders. The local side is read
/// afresh inside the apply's own transaction, so a row edited between the start and the apply is
/// decided on its current state rather than on the state the review happened to see.</para>
///
/// <para><strong>The link columns are the row this entry resolved to, and they are real foreign
/// keys.</strong> There are two of them rather than one, because a product entry names a
/// <c>Product</c> and a group entry names a <c>Category</c>, and a single column could be a key of
/// either. Each is a foreign key on <c>(BusinessId, …)</c> against that table's business key, so a
/// review item can never name another business's row, and both cascade: a review is a draft about the
/// catalogue, never a reason a product cannot be deleted.</para>
/// </summary>
public class ProviderCatalogueReviewItem : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }

    /// <summary>The review this entry belongs to.</summary>
    public long ReviewId { get; set; }

    [JsonIgnore]
    public virtual ProviderCatalogueReview? Review { get; set; }

    /// <summary>Whether this entry is a provider product or a provider product group.</summary>
    public ProviderCatalogueEntryKind Kind { get; set; }

    /// <summary>
    /// The provider's own identifier for the entry, as the opaque text the external id columns hold.
    /// For Nayax it is written through <c>Inventory.Domain.Providers.NayaxExternalId</c> - the
    /// <c>NayaxProductID</c> for a product, the <c>ProductGroupID</c> for a group - and it is never
    /// read as a local key (issue #735).
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The name the provider reported. The provider owns a linked row's name, so this is what the apply writes.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// For a product entry, the provider id of the group it belongs to, or <c>null</c> when the
    /// provider reported none. Resolved to a local category through
    /// <c>Category.ExternalCategoryId</c>, including a category this same apply links or creates.
    /// </summary>
    public string? ProviderGroupExternalId { get; set; }

    /// <summary>
    /// <strong>Historical only since issue #792 (A2 of #786), and deliberately kept.</strong> It
    /// held the provider's catalogue selling price as comparable text - the Nayax
    /// <c>ProductDefaultRetailPrice</c>, never the <c>ProductCostPrice</c> cost field - for the
    /// negotiation against <c>Product.UnitPrice</c>, and that product field no longer exists.
    ///
    /// <para>A review this release starts leaves it <c>null</c>, nothing reads it as a value to
    /// write, and the column and the values earlier releases wrote are neither dropped nor
    /// backfilled: they are a record of what the provider said at the moment a review was read. The
    /// one thing that still consults it is <c>EfCatalogueReviewStore.ApplyAsync</c>, which refuses a
    /// stored review that carries any as stale (<c>ApplyCatalogueReview.StaleSellingPriceReviewMessage</c>)
    /// rather than applying half of a plan that was approved whole.</para>
    /// </summary>
    public string? ProviderUnitPrice { get; set; }

    /// <summary>
    /// The provider's unit/volume label as comparable text. <strong>Always <c>null</c> today</strong>:
    /// the authoritative Lynx contract publishes no unit or volume label - see
    /// <c>CatalogueReviewPolicy.UnitVolumeUnavailableNote</c>. The column exists so that the day a
    /// human decides which Lynx field means it, only the projection changes.
    /// </summary>
    public string? ProviderUnitVolume { get; set; }

    /// <summary>The local product this entry resolved to, or <c>null</c> when no product carries its id.</summary>
    public long? LinkedProductId { get; set; }

    /// <summary>The local category this entry resolved to, or <c>null</c> when no category carries its id.</summary>
    public long? LinkedCategoryId { get; set; }

    /// <summary>
    /// How many of the business's stored sales are still unresolved and carry this provider product
    /// id with known raw provenance (issue #758) - the sales linking this entry would make
    /// resolvable. Reported so the operator can see them; never resolved by the catalogue apply,
    /// which is a separate, explicit, audited operation.
    /// </summary>
    public int UnresolvedSaleCount { get; set; }
}
