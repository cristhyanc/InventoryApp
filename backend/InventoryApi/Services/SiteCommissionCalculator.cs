using Inventory.Domain.Reporting;
using InventoryApi.Models;

namespace InventoryApi.Services;

public static class SiteCommissionCalculator
{
    public static decimal EligibleSales(CommissionBasis basis, decimal sale, NayaxPaymentType paymentType) =>
        basis switch
        {
            CommissionBasis.CardSales => paymentType == NayaxPaymentType.Card ? sale : 0m,
            CommissionBasis.SalesExGst => sale - ReportingCalculations.GstFromInclusive(sale),
            _ => sale
        };

    public static decimal CommissionAmount(SiteCommissionAgreement agreement, decimal sale, NayaxPaymentType paymentType) =>
        EligibleSales(agreement.Basis, sale, paymentType) * agreement.CommissionRate;
}
