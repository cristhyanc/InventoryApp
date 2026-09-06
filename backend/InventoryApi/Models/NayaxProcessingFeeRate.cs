namespace InventoryApi.Models;

public class NayaxProcessingFeeRate
{
    public int Id { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public decimal FeeExGst { get; set; }
    public DateTime CreatedAt { get; set; }
}
