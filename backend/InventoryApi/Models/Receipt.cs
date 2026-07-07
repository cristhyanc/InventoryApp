namespace InventoryApi.Models;

public class Receipt
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal? TotalAmount { get; set; }
    public DateTime PurchaseDate { get; set; } = DateTime.UtcNow;

    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }

    // Stored file info for the uploaded scan/photo
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
