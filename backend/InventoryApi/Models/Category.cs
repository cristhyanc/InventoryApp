using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }


    [JsonIgnore]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
