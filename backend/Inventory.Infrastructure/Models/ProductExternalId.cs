using System.Text.Json.Serialization;

using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One external system's identifier for one local <see cref="Product"/> (issue #732, parent #329
/// decision P1).
///
/// <para>Today <see cref="Product.Id"/> <em>is</em> the Nayax product id, which makes the local key
/// an identifier another system issues: it is global rather than per business, it cannot be chosen
/// by a business that has no provider at all, and two providers can never name the same product.
/// This table is where a provider id lives instead. It changes nothing on its own - every existing
/// id and foreign key stays exactly as it is, and the backfill simply records the mapping that is
/// already true - so the later slices can stop reading <c>Product.Id</c> as a Nayax id (#733, #745,
/// #747, #734) before #735 makes a new product's id the database's own.</para>
///
/// <para><see cref="ExternalId"/> is opaque text, compared exactly and never parsed by anything
/// that is not provider-specific. <c>Inventory.Domain.Providers.NayaxExternalId</c> is the one
/// place a Nayax numeric id becomes that text and is read back from it.</para>
///
/// <para>A link belongs to the same business as the product it points at, and that is a database
/// guarantee rather than an application rule: the foreign key is the pair
/// <c>(BusinessId, ProductId)</c> against the <c>(BusinessId, Id)</c> alternate key on
/// <c>Products</c>, so a row naming another business's product cannot be inserted even by code that
/// bypasses <c>BusinessOwnershipEnforcer</c>. Deleting the product deletes its links with it.</para>
/// </summary>
public class ProductExternalId : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out. It is also half
    /// of the composite foreign key above, which is what keeps a link inside one business.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>The external system that issued <see cref="ExternalId"/>.</summary>
    public ExternalProvider Provider { get; set; }

    /// <summary>
    /// The provider's own identifier for this product, stored as text and compared exactly. Shared
    /// code never parses it and never assumes it is numeric.
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The local product this provider id names.</summary>
    public long ProductId { get; set; }

    [JsonIgnore]
    public virtual Product? Product { get; set; }
}
