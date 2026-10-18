using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

public class Category : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// This category's identifier in the business's sales provider, or <c>null</c> when the provider
    /// does not name it (issue #759, parent #329 decision P1 as revised on 18 October 2026). The
    /// category half of <see cref="Product.ExternalProductId"/>, with the same rules: opaque text,
    /// issued by <c>Business.SalesProvider</c>, unique per business among the categories that have
    /// one, and never read as a local id.
    ///
    /// For Nayax it holds the <c>ProductGroupID</c>, written only through
    /// <c>Inventory.Domain.Providers.NayaxExternalId</c>. Deliberately off the wire, like the
    /// product column.
    /// </summary>
    [JsonIgnore]
    public string? ExternalCategoryId { get; set; }


    [JsonIgnore]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
