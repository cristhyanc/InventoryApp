using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class OperatingExpense
{
    public int Id { get; set; }
    public DateTime ExpenseDate { get; set; }
    public OperatingExpenseCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal AmountExGst { get; set; }
    public decimal GstAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }
    public long? SiteId { get; set; }
    public long? MachineId { get; set; }
    public string? AttachmentFileName { get; set; }
    [JsonIgnore]
    public string? AttachmentStoredFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public long? AttachmentFileSizeBytes { get; set; }
    public DateTime? ServicePeriodStart { get; set; }
    public DateTime? ServicePeriodEnd { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
