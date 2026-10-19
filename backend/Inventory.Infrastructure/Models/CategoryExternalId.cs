using System.Text.Json.Serialization;

using Inventory.Domain.Providers;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One external system's identifier for one local <see cref="Category"/> (issue #732, parent #329
/// decision P1). The category half of <see cref="ProductExternalId"/>, with the same rules and for
/// the same reason: <see cref="Category.Id"/> is today the Nayax <c>ProductGroupID</c>, and a
/// provider id belongs beside the local row rather than in place of its key.
/// </summary>
public class CategoryExternalId : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out. It is also half
    /// of the composite foreign key to <c>Categories (BusinessId, Id)</c>, which is what keeps a
    /// link inside one business.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>The external system that issued <see cref="ExternalId"/>.</summary>
    public ExternalProvider Provider { get; set; }

    /// <summary>
    /// The provider's own identifier for this category, stored as text and compared exactly. Shared
    /// code never parses it and never assumes it is numeric.
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The local category this provider id names.</summary>
    public long CategoryId { get; set; }

    [JsonIgnore]
    public virtual Category? Category { get; set; }
}
