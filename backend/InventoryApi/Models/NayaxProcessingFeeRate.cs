using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class NayaxProcessingFeeRate : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public decimal FeeExGst { get; set; }
    public DateTime CreatedAt { get; set; }
}
