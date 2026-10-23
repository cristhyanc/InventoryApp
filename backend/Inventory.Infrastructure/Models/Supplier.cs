using System.Text.Json.Serialization;

using Inventory.Domain.Gst;

namespace Inventory.Infrastructure.Models;

public class Supplier : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>
    /// The supplier's optional website (issue #847). <c>null</c> when none is recorded; otherwise
    /// an absolute <c>http</c>/<c>https</c> address of at most
    /// <see cref="Inventory.Application.Suppliers.SupplierUrlPolicy.MaxLength"/> characters, which
    /// that policy accepted before the write. The server never fetches, resolves or follows it.
    ///
    /// Deliberately <em>on</em> the wire, unlike the GST defaults below: this entity's serialisable
    /// surface is the published legacy <c>Supplier</c> component, and the 23 October 2026 decision
    /// on issue #847 is that <c>url</c> is an additive nullable field of it and so appears in every
    /// nested <c>supplier</c> object. The column length is configured in <c>AppDbContext</c> rather
    /// than with a data annotation, which would have added a <c>maxLength</c> keyword to that
    /// published component.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// This supplier's explicitly configured GST default for its product lines (issue #430),
    /// <see cref="GstRules.None"/> until an administrator sets one. A supplier being GST-registered
    /// is never enough to classify its products, so nothing infers this value.
    ///
    /// Deliberately off the wire, like <see cref="DeliveryGstDefault"/> and
    /// <see cref="PackageGstDefault"/>: this entity's serialisable surface is the pinned legacy
    /// <c>Supplier</c> API component that <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c>
    /// regenerates and that every nested <c>supplier</c> object in a product, purchase,
    /// supplier-order and operating-expense payload references, so the defaults are published on
    /// their own <c>/api/suppliers/{id}/gst-defaults</c> resource instead.
    /// </summary>
    [JsonIgnore]
    public GstClassification ProductLineGstDefault { get; set; } = GstRules.None;

    /// <summary>
    /// The supplier's default for a purchase's delivery charge. Separate from
    /// <see cref="ProductLineGstDefault"/> on purpose: a charge never inherits a product line's
    /// classification (parent issue #62).
    /// </summary>
    [JsonIgnore]
    public GstClassification DeliveryGstDefault { get; set; } = GstRules.None;

    /// <summary>The supplier's default for a purchase's package charge; see <see cref="DeliveryGstDefault"/>.</summary>
    [JsonIgnore]
    public GstClassification PackageGstDefault { get; set; } = GstRules.None;

    // No products collection (issue #787, A1 of #786): a supplier is named by the purchases and the
    // supplier orders it was party to, not by a catalogue row. Those two relationships, this
    // supplier's GST defaults and supplier CRUD are all unchanged.
}
