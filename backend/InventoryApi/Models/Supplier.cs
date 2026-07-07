using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    [JsonIgnore]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
