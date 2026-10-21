using Inventory.Application.Machines;
using Inventory.Application.Products;
using Inventory.Domain.Providers;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects the Application layer's <see cref="ProductRecord"/> onto the API-owned
/// <see cref="ProductResponse"/> the product endpoints serialize (issue #303). It replaces the step
/// that used to rebuild the EF <c>Inventory.Infrastructure.Models.Product</c> entity for those endpoints, and
/// copies only persisted facts: the derived reorder values are computed by the response itself from
/// <c>Inventory.Domain.Products.ProductReorderPolicy</c>, so this mapping cannot introduce a second
/// copy of a reorder formula.
///
/// Since issue #302 the machine-product view is projected here too, through the
/// <see cref="ToResponse(MachineProductRecord, FinancialFigureVisibility)"/> overload below, so the
/// two endpoints that serve a product cannot drift apart: the catalogue shape is built once and the
/// machine slot's own values are overlaid on it.
///
/// <para>Since issue #698 both projections depend on <see cref="FinancialFigureVisibility"/>:
/// <see cref="ProductResponse.InventoryValue"/> is the value of the business's stock at cost - the
/// figure the Dashboard withholds (issue #527) and <c>inventory-value-summary</c> refuses
/// (issue #681), recoverable by summing this one - so a caller who may not be shown it is answered
/// <c>null</c>. Unit cost, the catalogue selling price and the machine slot's own price are not
/// financial figures by that decision and are unchanged for every role.</para>
///
/// <para>Issue #700 closed the gap #698 left open deliberately. A product's
/// <see cref="ProductResponse.StockAdjustments"/> carry each movement's
/// <c>inventoryValueAfter</c>, and the most recent movement's is the product's current valuation -
/// so withholding the column while publishing the history beside it withheld nothing. The nested
/// movements are therefore projected under the same visibility, by the same rule
/// <see cref="StockAdjustmentResponseMapper"/> applies on the stock endpoints.</para>
/// </summary>
public static class ProductRecordResponseMapper
{
    public static ProductResponse ToResponse(ProductRecord record, FinancialFigureVisibility visibility) =>
        new()
        {
            Id = record.Id,
            Name = record.Name,
            Sku = record.Sku,
            Description = record.Description,
            UnitPrice = record.UnitPrice,
            AverageUnitCost = record.AverageUnitCost,
            CostingQuantity = record.CostingQuantity,
            InventoryValue = visibility.Withhold(record.InventoryValue),
            QuantityInStock = record.QuantityInStock,
            LowStockThreshold = record.LowStockThreshold,
            RestockTo = record.RestockTo,
            Unit = record.Unit,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            CategoryId = record.CategoryId,
            Category = record.Category is null
                ? null
                : new CategoryResponse(record.Category.Id, record.Category.Name, record.Category.Description),
            StockAdjustments = record.StockAdjustments
                .Select(adjustment => ToMovementResponse(adjustment, visibility))
                .ToList(),

            // Resolved live by the reorder-alert listing, not persisted; zero on the read paths that
            // do not resolve them, exactly as before. The derived values follow from these.
            MachineReplenishmentNeed = record.MachineReplenishmentNeed,
            OnOrderQuantity = record.OnOrderQuantity,

            // Which fields the business's sales provider owns on this product (issue #736), from the
            // one Domain statement of that rule - the same one the update refuses a change with, so
            // a client cannot lock a different set from the one the server enforces.
            ProviderManagedFields = ProviderManagedCatalogueFields.ForProduct(record.IsProviderManaged),

            // Whether the delete would be allowed, and the refusal it would give otherwise (issue
            // #737), from the deletion rule itself rather than a second copy of it. Both keys are
            // absent on a read that did not resolve the question - which is not the same as "yes",
            // so neither is given a default here.
            CanDelete = record.DeletionEligibility?.Allowed,
            DeletionRefusal = record.DeletionEligibility?.Refusal,
        };

    /// <summary>
    /// One slot in a machine's product listing (issue #302): the catalogue product it dispenses,
    /// with the machine's own price, raw Nayax commission metadata, MDB code, slot capacity and
    /// resolved suggested pricing overlaid, and with the slot's stock replacing the product's
    /// storage stock - the same overlay, in the same order, the retired <c>MachineService</c>
    /// applied to the entity. The slot stock override is why the derived reorder values the response
    /// computes describe this machine slot rather than the storage shelf, exactly as before.
    /// </summary>
    public static ProductResponse ToResponse(
        MachineProductRecord record, FinancialFigureVisibility visibility) =>
        ToResponse(record.Product, visibility) with
        {
            QuantityInStock = record.QuantityInStock,

            // A machine slot is not where a product is edited, and this response is a pinned
            // contract of its own (InventoryApi.Tests.DTOs.MachineJsonContractTests), so the
            // provider-managed declaration issue #736 added to the catalogue view is left unsaid
            // here rather than answered for a surface that cannot act on it.
            ProviderManagedFields = null,

            // A machine slot is not where a product is deleted either, so issue #737's deletion
            // answer is left unsaid here for the same reason, rather than carried onto a pinned
            // payload whose reader cannot act on it.
            CanDelete = null,
            DeletionRefusal = null,
            MachineSlot = new MachineSlotOverlay(
                record.MachinePrice,
                record.CommissionValue,
                record.SuggestedNetValue,
                record.SuggestedPriceValue,
                record.MdbCode,
                record.MaxStockInMachine),
        };

    /// <summary>
    /// One movement in the product's own history, in the same wire shape the stock endpoints
    /// publish, with the same figure withheld under the same visibility (issue #700).
    /// </summary>
    private static ProductStockAdjustmentResponse ToMovementResponse(
        ProductStockAdjustmentRecord record, FinancialFigureVisibility visibility) => new(
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
