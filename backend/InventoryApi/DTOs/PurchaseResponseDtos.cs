using Inventory.Domain.Gst;

namespace InventoryApi.DTOs;

/// <summary>
/// The wire shape of a purchase inside the <see cref="PurchaseResponseDto"/> envelope's
/// <c>purchase</c> key (issue #304), replacing the EF <c>Inventory.Infrastructure.Models.Purchase</c> entity the
/// retired <c>PurchaseService</c> rebuilt for the "/api/purchases" endpoints. Key names, order,
/// nesting and values match that entity's serializable surface exactly, so this is not a contract
/// change; <c>InventoryApi.Tests.DTOs.PurchaseResponseJsonContractTests</c> compares the serialized
/// bytes of both.
///
/// The owning business is absent rather than <c>[JsonIgnore]</c>d: a tenant owner is resolved from
/// the authenticated actor and never travels on the wire in either direction (AGENTS.md § Tenant
/// ownership and data isolation).
/// </summary>
public sealed record PurchaseResponse
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public decimal? TotalAmount { get; init; }
    public decimal? DeliveryCost { get; init; }

    /// <summary>
    /// The delivery charge's own GST classification and provenance (issue #429), emitted next to
    /// the amount they describe. Both are <c>Unknown</c> for a charge nobody has classified and for
    /// a charge that is absent or zero.
    /// </summary>
    public GstClassification DeliveryGstClassification { get; init; }
    public GstClassificationSource DeliveryGstClassificationSource { get; init; }

    public decimal? PackageCost { get; init; }

    /// <summary>The package charge's own GST classification and provenance; see <see cref="DeliveryGstClassification"/>.</summary>
    public GstClassification PackageGstClassification { get; init; }
    public GstClassificationSource PackageGstClassificationSource { get; init; }

    public required DateTime PurchaseDate { get; init; }

    public int? SupplierId { get; init; }
    public SupplierResponse? Supplier { get; init; }

    public IReadOnlyList<PurchaseItemResponse> Items { get; init; } = [];

    // Stored-document metadata for the uploaded scan/photo of the supporting document. It names a
    // file in protected storage, never a physical server path.
    public required string FileName { get; init; }
    public required string StoredFileName { get; init; }
    public required string ContentType { get; init; }
    public required long FileSizeBytes { get; init; }

    public required DateTime CreatedAt { get; init; }
}

/// <summary>
/// One purchase line item as the purchase endpoints serialize it. Matches the serializable surface
/// of the <c>Inventory.Infrastructure.Models.PurchaseItem</c> entity it replaced: the owning business, the
/// purchase back-reference and the supplier-order allocations stay off the wire, <c>ReceiptId</c>
/// keeps its name as part of the established JSON contract (see
/// <c>docs/architecture.md</c> § Purchase rename plan), and <see cref="Product"/> is null exactly
/// when the read path did not load the product navigation.
/// </summary>
public sealed record PurchaseItemResponse
{
    public required int Id { get; init; }
    public required int ReceiptId { get; init; }
    public required long ProductId { get; init; }
    public ProductResponse? Product { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitCost { get; init; }

    /// <summary>
    /// This line's own GST classification and provenance (issue #429). GST is classified per line,
    /// so the value here never describes the purchase as a whole, and it is accounting data only:
    /// it does not affect <see cref="UnitCost"/> or <see cref="LineTotal"/>.
    /// </summary>
    public GstClassification GstClassification { get; init; }
    public GstClassificationSource GstClassificationSource { get; init; }

    /// <summary>
    /// This line's extended cost, derived here rather than carried as data, exactly as the entity's
    /// <c>[NotMapped]</c> computed property was. The purchase total/mismatch rule over these lines
    /// stays in <c>Inventory.Domain.Purchases.PurchaseTotalValidationPolicy</c>, which feeds the
    /// envelope's separate <see cref="PurchaseValidationDto"/> block.
    /// </summary>
    public decimal LineTotal => Quantity * UnitCost;
}
