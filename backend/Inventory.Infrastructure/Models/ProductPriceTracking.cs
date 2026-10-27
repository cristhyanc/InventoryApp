using System.Text.Json.Serialization;

using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One business's statement that it wants one of <em>its own</em> products priced, and which shared
/// master product that product is (issue #923, parent #817).
///
/// <para><strong>This is the one tenant-owned table in the shared catalogue, and it is deliberately
/// ordinary.</strong> It implements <see cref="IBusinessOwned"/>, so <c>AppDbContext</c> gives it a
/// required business key, an index leading with it and a global query filter, and
/// <c>BusinessOwnershipEnforcer</c> stamps it on insert and refuses every cross-business write - the
/// same treatment a purchase or a stock adjustment gets. Nothing new, no exception and no second
/// mechanism: the three platform tables beside it are about products, and this row is about a
/// business's decision, which is exactly the kind of thing tenant ownership exists for.</para>
///
/// <para><strong>A business is unique per product.</strong> One mapping per <c>(BusinessId,
/// ProductId)</c>, scoped by business for the reason every uniqueness rule in this schema is: two
/// businesses may legitimately track the same product, and a global constraint would let whichever
/// tracked it first deny the other.</para>
///
/// <para><strong>The search text is captured, not derived.</strong> <see cref="SearchName"/> and
/// <see cref="UnitLabel"/> are taken from the product's own <c>Name</c> and <c>Unit</c> when tracking
/// is turned on (the owner's decision of 27 October 2026) and stored as plain text. A product that is
/// renamed afterwards keeps being searched for under the text the business was shown rather than
/// silently changing what a collector looks for - and the stored text is what lets a work list of
/// searches be read without reaching into anybody's catalogue. <see cref="WaitingKey"/> is those two
/// values folded, which is how five businesses' five rows become one search.</para>
///
/// <para><strong>It changes nothing about the product.</strong> <see cref="Product"/> is referenced,
/// never altered: no column, contract, cost, quantity or price of a product is touched by this issue,
/// and the reference cascades on delete because a mapping is a statement about a product and has no
/// meaning without it - the same reason a provider link cascades (docs/architecture.md § Provider
/// identity on the local row). Deleting a product therefore behaves exactly as it did before.</para>
/// </summary>
public class ProductPriceTracking : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The business's own product this mapping is about. A foreign key on
    /// <c>(BusinessId, ProductId)</c> to the products' <c>(BusinessId, Id)</c> business key, so a
    /// mapping cannot name another business's product even in principle.
    /// </summary>
    public long ProductId { get; set; }

    /// <summary>The product itself.</summary>
    public virtual Product? Product { get; set; }

    /// <summary>
    /// The shared master product this product has been matched to, or <see langword="null"/> while it
    /// is still waiting. Referenced with <c>Restrict</c>: a master product businesses are tracking
    /// through is not something to remove from under them, and silently nulling a mapping would turn a
    /// settled match back into a search without anybody deciding to.
    /// </summary>
    public long? MasterProductId { get; set; }

    /// <summary>The master product itself, while there is one.</summary>
    public virtual MasterProduct? MasterProduct { get; set; }

    /// <summary>
    /// Where this mapping has got to: requested, auto-matched, matched or not found. It names a
    /// master product in exactly the two matched states - <c>ProductPriceTrackingPolicy</c> is that
    /// rule.
    /// </summary>
    public ProductPriceTrackingStatus Status { get; set; } = ProductPriceTrackingStatus.Requested;

    /// <summary>
    /// How the match was made - the collector's automatic match (#941) or a Super Admin (#928) - and
    /// <see langword="null"/> while there is no match. "Who decided this?" is the first question asked
    /// of a product being priced as something else.
    /// </summary>
    public ProductPriceTrackingMatchSource? MatchSource { get; set; }

    /// <summary>
    /// <c>PriceCatalogueWaitingKey.For</c> of <see cref="SearchName"/> and <see cref="UnitLabel"/>:
    /// the key this product is searched for, and grouped by, while it waits for a match. Case and
    /// spacing are folded, so two businesses' spellings of one product are one key and one search.
    /// </summary>
    public string WaitingKey { get; set; } = string.Empty;

    /// <summary>
    /// The product's own name as it was when tracking was turned on, trimmed. Plain text, which is
    /// what the collector searches with.
    /// </summary>
    public string SearchName { get; set; } = string.Empty;

    /// <summary>
    /// The product's own unit label (<c>Product.Unit</c>) as it was when tracking was turned on,
    /// trimmed, and the <strong>empty string</strong> when the product has none - because the waiting
    /// key is built from both parts and "no unit label" must have exactly one spelling, or one product
    /// becomes two searches.
    /// </summary>
    public string UnitLabel { get; set; } = string.Empty;

    /// <summary>When tracking was turned on, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the mapping last changed; equal to <see cref="CreatedAtUtc"/> until the first change.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
