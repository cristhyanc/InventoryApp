using Inventory.Application.Costing;
using InventoryApi.Models;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Maps the <see cref="NayaxSales"/> persistence entity to and from the Application sale-costing
/// contract (issue #297), for the callers that cost a sale they are importing - the Nayax
/// transaction import in <c>ImportService</c> and <see cref="EfLatestNayaxSalesStore"/>. It decides
/// nothing: <see cref="ICostSale"/> decides the cost and this only writes it onto the entity.
/// </summary>
public static class NayaxSaleCosting
{
    public static CostableSale ToCostableSale(this NayaxSales sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return new CostableSale(
            sale.TransactionID,
            sale.TransactionStatusId,
            sale.NayaxProductId,
            sale.ProductName,
            sale.MachineAuthorizationTime,
            sale.NayaxProductCostPrice,
            sale.UnitCostAtSale,
            sale.CostOfGoodsSold,
            (SaleCostStatus)sale.CostingStatus);
    }

    /// <summary>Writes the decided cost and its provenance onto the sale; a sale is one unit.</summary>
    public static void ApplySaleCost(this NayaxSales sale, SaleCostAssignment cost)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(cost);
        sale.UnitCostAtSale = cost.UnitCost;
        sale.CostOfGoodsSold = cost.UnitCost;
        sale.CostingStatus = (SaleCostingStatus)cost.Status;
        sale.CostSource = (SaleCostSource)cost.Source;
    }

    /// <summary>
    /// Costs <paramref name="sale"/> through <paramref name="costSale"/> and writes the decision onto
    /// it; a sale the use case keeps unchanged is left untouched.
    /// </summary>
    public static async Task CostAsync(
        this ICostSale costSale,
        NayaxSales sale,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(costSale);
        var cost = await costSale.Handle(sale.ToCostableSale(), force, cancellationToken);
        if (cost is not null)
            sale.ApplySaleCost(cost);
    }
}
