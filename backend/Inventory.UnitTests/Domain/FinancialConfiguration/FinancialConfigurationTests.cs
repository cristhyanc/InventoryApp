using Inventory.Domain.FinancialConfiguration;
using Xunit;

namespace InventoryApi.Tests.Domain.FinancialConfiguration;

public class FinancialConfigurationTests
{
    [Theory]
    [InlineData(" Cash ", null, NayaxPaymentType.Cash)]
    [InlineData("Credit Card", null, NayaxPaymentType.Card)]
    [InlineData("Prepaid Credit", null, NayaxPaymentType.Card)]
    [InlineData("Other", "Cashless card", NayaxPaymentType.Cash)]
    [InlineData("Other", "Card payment", NayaxPaymentType.Card)]
    [InlineData("Other", "Cash payment", NayaxPaymentType.Cash)]
    [InlineData(null, null, NayaxPaymentType.Unknown)]
    [InlineData("Voucher", "Other", NayaxPaymentType.Unknown)]
    public void Payment_method_classification_preserves_card_cash_and_unknown_paths(
        string? paymentMethod,
        string? recognitionDescription,
        NayaxPaymentType expected)
    {
        Assert.Equal(expected, PaymentMethodClassifier.Classify(paymentMethod, recognitionDescription));
    }

    [Theory]
    [InlineData(NayaxTransactionStatusIds.Completed, NayaxTransactionStatus.Completed)]
    [InlineData(NayaxTransactionStatusIds.PendingSettlementNotFinal, NayaxTransactionStatus.Pending)]
    [InlineData(NayaxTransactionStatusIds.PendingBatch, NayaxTransactionStatus.Pending)]
    [InlineData(NayaxTransactionStatusIds.Refunded, NayaxTransactionStatus.Refunded)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined26, NayaxTransactionStatus.CancelledOrDeclined)]
    [InlineData(NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed, NayaxTransactionStatus.CancelledOrDeclined)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined31, NayaxTransactionStatus.CancelledOrDeclined)]
    [InlineData(NayaxTransactionStatusIds.CancelledOrDeclined250, NayaxTransactionStatus.CancelledOrDeclined)]
    [InlineData(null, NayaxTransactionStatus.Unknown)]
    [InlineData(21, NayaxTransactionStatus.Unknown)]
    public void Transaction_status_classification_preserves_every_supported_id(
        int? statusId,
        NayaxTransactionStatus expected)
    {
        Assert.Equal(expected, NayaxTransactionStatusClassifier.Classify(statusId));
    }

    [Fact]
    public void Commission_resolution_includes_both_boundary_dates_and_rejects_overlaps()
    {
        var first = Agreement(new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), .10m);
        var second = Agreement(new DateTime(2026, 7, 1), null, .12m);

        Assert.Equal(first, EffectiveFinancialConfiguration.ResolveAgreement([first, second], 42, new DateTime(2026, 6, 30, 23, 59, 0)));
        Assert.Equal(second, EffectiveFinancialConfiguration.ResolveAgreement([first, second], 42, new DateTime(2026, 7, 1)));
        Assert.Null(EffectiveFinancialConfiguration.ResolveAgreement([first], 99, new DateTime(2026, 3, 1)));
        Assert.Throws<InvalidOperationException>(() =>
            EffectiveFinancialConfiguration.ResolveAgreement(
                [first, Agreement(new DateTime(2026, 6, 1), null, .20m)], 42, new DateTime(2026, 6, 30)));
    }

    [Theory]
    [InlineData(CommissionBasis.GrossSales, NayaxPaymentType.Cash, 100, 1)]
    [InlineData(CommissionBasis.CardSales, NayaxPaymentType.Cash, 100, 0)]
    [InlineData(CommissionBasis.CardSales, NayaxPaymentType.Card, 100, 1)]
    [InlineData(CommissionBasis.SalesExGst, NayaxPaymentType.Card, 110, 1)]
    public void Commission_calculation_preserves_existing_basis_semantics(
        CommissionBasis basis,
        NayaxPaymentType paymentType,
        decimal sale,
        decimal expected)
    {
        var agreement = Agreement(new DateTime(2026, 1, 1), null, .01m, basis);

        Assert.Equal(expected, SiteCommissionCalculator.CommissionAmount(agreement, sale, paymentType));
    }

    private static CommissionAgreement Agreement(
        DateTime from,
        DateTime? to,
        decimal rate,
        CommissionBasis basis = CommissionBasis.GrossSales,
        long siteId = 42) =>
        new(0, siteId, from, to, rate, CommissionFrequency.Monthly, basis, 14, default, default);
}
