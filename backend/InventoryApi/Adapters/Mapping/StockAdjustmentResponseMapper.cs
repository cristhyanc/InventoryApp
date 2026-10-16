using Inventory.Application.Stock;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="StockAdjustmentRecord"/> onto the API-owned
/// <see cref="ProductStockAdjustmentResponse"/> the stock endpoints serialise (issue #305). It
/// replaced the step that rebuilt the <c>Inventory.Infrastructure.Models.StockAdjustment</c> entity for those
/// endpoints (issue #282), so no production code maps a stock read model back onto a persistence
/// entity any more.
///
/// The response is the same wire shape the product endpoints have published for a movement in a
/// product's history since issue #303 - one shape, not two, so the two places a client reads a
/// stock movement cannot drift apart. It carries every key the entity serialised, in the same
/// order: the owning business and the <c>Product</c>/<c>ReceiptItem</c> navigations were
/// <c>[JsonIgnore]</c>d on the entity and are simply absent here, which is also the tenancy rule
/// (AGENTS.md § Tenant ownership and data isolation).
///
/// The published OpenAPI document is unchanged by this: the stock operations still describe
/// <c>#/components/schemas/StockAdjustment</c>, because
/// <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> substitutes that legacy schema for
/// their response. Issue #305 excludes API-contract changes, and a schema reference is
/// client-visible even when the payload is byte-identical.
///
/// <see cref="ProductStockAdjustmentResponse.Reason"/>/<c>Source</c> are the
/// <c>Inventory.Infrastructure.Models</c> enums rather than API-owned copies - a temporary compatibility
/// exception, shared with the request DTO's <c>StockAdjustmentDto.Reason</c> - because the published
/// document reaches those CLR enums from the pinned <c>StockAdjustment</c> response component itself
/// and from the legacy <c>Product</c> component the pinned purchase/supplier-order schemas
/// reference. A second enum of the same simple name makes Swashbuckle fail document generation with
/// a duplicate-schema-id error, so the shared wire vocabulary stays where it is until the
/// persistence models relocate; the persisted numeric values a client switches on are unchanged
/// either way.
///
/// <para>Since issue #700 the projection depends on <see cref="FinancialFigureVisibility"/>:
/// <see cref="ProductStockAdjustmentResponse.InventoryValueAfter"/> is the business's inventory
/// value at cost immediately after the movement - on the latest movement, the current valuation
/// itself, which the Dashboard withholds (issue #527), <c>inventory-value-summary</c> refuses
/// (issue #681) and the catalogue no longer carries (issue #698). The movement's <em>own</em> cost
/// figures are not withheld: <c>unitCost</c>, <c>totalCost</c> and <c>averageUnitCostAfter</c> are
/// the cost of the restock an Operator records and a unit cost, which the recorded decision keeps
/// visible to every role.</para>
/// </summary>
public static class StockAdjustmentResponseMapper
{
    public static ProductStockAdjustmentResponse ToResponse(
        StockAdjustmentRecord record, FinancialFigureVisibility visibility) => new(
            record.Id,
            record.ProductId,
            record.ReceiptItemId,
            record.QuantityChange,
            record.QuantityAfter,
            record.UnitCost,
            record.TotalCost,
            record.CostingQuantityAfter,
            record.AverageUnitCostAfter,
            visibility.Withhold(record.InventoryValueAfter),
            (StockAdjustmentReason)record.Reason,
            (StockAdjustmentSource)record.Source,
            record.MachineId,
            record.Notes,
            record.EatBefore,
            record.CreatedAt,
            record.EffectiveAt);
}
