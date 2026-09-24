using System.Text.Json.Serialization;

namespace InventoryApi.Models;

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


    [JsonIgnore]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
