using Inventory.Domain.Gst;

namespace InventoryApi.DTOs;

/// <summary>
/// The body of <c>PUT /api/products/{id}/gst-rule</c> (issue #430). It replaces the product's whole
/// rule, which is one value, so an omitted <c>gstRule</c> binds to <c>0</c> - no rule - exactly as
/// sending it explicitly would.
///
/// A number outside the published <c>GstClassification</c> enum is refused with <c>400</c> and
/// stores nothing: a C# enum constrains a compiler, not a JSON body, and a rule no policy describes
/// must never be persisted (AGENTS.md § Purchase GST classification).
/// </summary>
public record ProductGstRuleDto(GstClassification GstRule);

/// <summary>
/// The product's configured GST rule as <c>GET /api/products/{id}/gst-rule</c> answers it.
/// <see cref="GstRule"/> is <c>Unknown</c> for a product nobody has configured, which is a real
/// state - "no rule" - and not the same as an explicit <c>GstFree</c> rule.
/// </summary>
public record ProductGstRuleResponse(long ProductId, GstClassification GstRule);

/// <summary>
/// The body of <c>PUT /api/suppliers/{id}/gst-defaults</c> (issue #430): the supplier's default for
/// its product lines and its separate defaults for a purchase's delivery and package charges. All
/// three are replaced together, and an omitted value binds to <c>0</c> - no default. Any value
/// outside the published enum is refused with <c>400</c> and stores nothing.
/// </summary>
public record SupplierGstDefaultsDto(
    GstClassification ProductLineGstDefault,
    GstClassification DeliveryGstDefault,
    GstClassification PackageGstDefault);

/// <summary>
/// The supplier's configured GST defaults as <c>GET /api/suppliers/{id}/gst-defaults</c> answers
/// them. A default applies only where the purchased product has no rule of its own; a charge
/// default never comes from the product-line default.
/// </summary>
public record SupplierGstDefaultsResponse(
    int SupplierId,
    GstClassification ProductLineGstDefault,
    GstClassification DeliveryGstDefault,
    GstClassification PackageGstDefault);
