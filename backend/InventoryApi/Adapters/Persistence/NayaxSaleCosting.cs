using System.Diagnostics.CodeAnalysis;
using Inventory.Application.Costing;
using Inventory.Infrastructure.Models;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Maps the <see cref="NayaxSales"/> persistence entity to and from the Application sale-costing
/// contract (issue #297), for the adapters that cost a sale they are importing -
/// <see cref="EfNayaxSalesImportStore"/> (the uploaded transaction export, issue #301) and
/// <see cref="EfLatestNayaxSalesStore"/> (the latest-sales synchronization). It decides
/// nothing: <see cref="ICostSale"/> decides the cost and this only writes it onto the entity.
/// </summary>
public static class NayaxSaleCosting
{
    public static CostableSale ToCostableSale(this NayaxSales sale) => new NayaxCostableSale(sale);

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

/// <summary>
/// The Application <see cref="CostableSale"/> view of one <see cref="NayaxSales"/> row, keeping a
/// reference to the row so a use case's decision can be written back onto exactly that entity.
/// </summary>
internal sealed class NayaxCostableSale : CostableSale
{
    [SetsRequiredMembers]
    public NayaxCostableSale(NayaxSales entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Entity = entity;
        TransactionId = entity.TransactionID;
        TransactionStatusId = entity.TransactionStatusId;
        NayaxProductId = entity.NayaxProductId;
        ProductName = entity.ProductName;
        AuthorizationTime = entity.MachineAuthorizationTime;
        NayaxProductCostPrice = entity.NayaxProductCostPrice;
        UnitCostAtSale = entity.UnitCostAtSale;
        CostOfGoodsSold = entity.CostOfGoodsSold;
        CostingStatus = (SaleCostStatus)entity.CostingStatus;
    }

    public NayaxSales Entity { get; }
}
