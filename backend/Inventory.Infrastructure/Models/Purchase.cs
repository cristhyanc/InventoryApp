using System.Text.Json.Serialization;
using Inventory.Domain.Gst;

namespace Inventory.Infrastructure.Models;

// The persistence entity behind the "Purchase" business record. The CLR type and this file
// are named Purchase (not Receipt) so InventoryApi speaks the same Purchase/PurchaseItem
// business language as Inventory.Domain.Purchases/Inventory.Application.Purchases. The
// DbSet property ("Receipts") and the table name stay "Receipts" as the legacy persistence
// compatibility surface (see docs/architecture.md's Purchase rename plan).
public class Purchase : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? DeliveryCost { get; set; }

    /// <summary>
    /// The delivery charge's own GST classification and its provenance (issue #429). A
    /// purchase-level charge never inherits a classification from the product lines. Both default
    /// to <c>Unknown</c>, which is what the additive migration leaves on every existing row and
    /// what an absent (null or zero) delivery charge always carries
    /// (<c>Inventory.Domain.Purchases.PurchaseGstPolicy.ClassifyCharge</c>).
    /// </summary>
    public GstClassification DeliveryGstClassification { get; set; }
    public GstClassificationSource DeliveryGstClassificationSource { get; set; }

    public decimal? PackageCost { get; set; }

    /// <summary>The package charge's own GST classification and provenance; see <see cref="DeliveryGstClassification"/>.</summary>
    public GstClassification PackageGstClassification { get; set; }
    public GstClassificationSource PackageGstClassificationSource { get; set; }

    public DateTime PurchaseDate { get; set; } = DateTime.UtcNow;

    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }
    public virtual ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();

    // Stored file info for the uploaded scan/photo of the supporting document.
    // Distinct from the purchase business record itself (see PurchaseItem/Purchase above).
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
