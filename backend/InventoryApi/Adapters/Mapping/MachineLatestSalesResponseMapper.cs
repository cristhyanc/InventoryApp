using Inventory.Application.Machines;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects <see cref="MachineLatestSales"/> onto the API-owned
/// <see cref="MachineLatestSalesResponse"/> that <c>GET api/machines/{id}/latest-sales</c>
/// serializes (issue #768).
///
/// <para><strong>This is where the financial decision is applied, on the server.</strong> Following
/// issue #698's pattern for the rest of the machine page, the endpoint stays open to every role that
/// may operate inventory - seeing what a machine has been dispensing is restocking work - and what
/// changes is the shape of the response. A caller without <c>Dashboard.ViewFinancials</c> is given
/// every sale's date and time, reported product name and payment method, and <c>null</c> for the
/// amount and the currency that labels it. Never <c>0</c>: that is
/// <see cref="FinancialFigureVisibilityResolution.Withhold"/>'s one job, shared with every other
/// mapper that withholds an amount, so the never-zero rule is written once.</para>
///
/// <para>Every projection has to state which visibility it wants, so a new call site cannot publish
/// the amounts by forgetting to ask - and because
/// <see cref="FinancialFigureVisibility.Redacted"/> is the enum's zero, a projection reached
/// without a decision fails closed.</para>
/// </summary>
public static class MachineLatestSalesResponseMapper
{
    public static MachineLatestSalesResponse ToResponse(
        long machineId, MachineLatestSales latest, FinancialFigureVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(latest);

        return new MachineLatestSalesResponse
        {
            MachineId = machineId,
            IsAvailable = latest.IsAvailable,
            UnavailableReason = latest.UnavailableReason is { } reason
                ? MachineLatestSalesUnavailableReasons.Code(reason)
                : null,
            UnavailableDetail = latest.UnavailableReason is { } detail
                ? MachineLatestSalesUnavailableReasons.Describe(detail)
                : null,
            Sales = [.. latest.Sales.Select(sale => ToResponse(sale, visibility))],
        };
    }

    private static MachineLatestSaleResponse ToResponse(
        MachineLatestSale sale, FinancialFigureVisibility visibility) =>
        new()
        {
            TransactionId = sale.TransactionId,
            SaleDateTimeUtc = sale.SaleAtUtc,
            ProductName = sale.ProductName,
            PaymentMethod = sale.PaymentMethod,
            SaleAmount = visibility.Withhold(sale.SaleAmount),

            // The currency goes with the amount. On its own it says nothing a caller who may not see
            // the amount needs, and leaving it would advertise the figure's shape.
            CurrencyCode = visibility == FinancialFigureVisibility.Visible ? sale.CurrencyCode : null,
        };
}
