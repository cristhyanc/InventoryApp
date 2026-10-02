using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Domain.Reporting.Transactions;

/// <summary>
/// Inputs required to derive a single transaction's estimated Nayax fee, site commission, and
/// gross/direct profit. Already-classified facts for one sale; carries no query or persistence
/// behavior.
/// </summary>
public readonly record struct TransactionRowInputs(
    decimal Sale,
    NayaxPaymentType PaymentType,
    NayaxTransactionStatus Status,
    DateTime AuthorizationDate,
    long? SiteId,
    decimal? CostOfGoodsSold,
    // Whether a persisted cost is recorded for this sale (independent of completion status),
    // mirroring InventoryApi.Models.SaleCostingStatus.Costed with a non-null CostOfGoodsSold.
    bool HasPersistedCost);

public readonly record struct TransactionRowResult(
    decimal? FeeExGst,
    decimal? FeeGst,
    decimal? FeeIncGst,
    string FeeSource,
    bool FeeUnavailable,
    decimal? CommissionRate,
    CommissionBasis? CommissionBasis,
    decimal? CommissionAmount,
    bool HasOverlappingCommission,
    bool CommissionUnavailable,
    // True only when a persisted cost exists AND the sale is completed; gates GrossProfit.
    bool IsCosted,
    decimal? GrossProfit,
    decimal? GrossMarginPercent,
    decimal? DirectProfit,
    decimal? DirectMarginPercent);

/// <summary>
/// Per-transaction row-building policy: the effective-dated Nayax fee-rate lookup and
/// fee-unavailable rule, the effective-dated site commission-agreement lookup and its
/// overlapping/unavailable rules, and the resulting gross/direct profit derivation. Distinct from
/// (but consistent with) the aggregate bookkeeping/machine-profitability completeness rules in
/// <c>Inventory.Domain.Reporting.Bookkeeping</c>/<c>Profitability</c>: here, each transaction is
/// judged on its own effective-dated coverage rather than period-level completeness.
/// </summary>
public static class TransactionRowPolicy
{
    public static TransactionRowResult Calculate(
        TransactionRowInputs sale,
        IReadOnlyList<EffectiveNayaxFeeRate> rates,
        IReadOnlyList<EffectiveCommissionAgreement> agreements)
    {
        decimal? feeExGst = 0m, feeGst = 0m, feeIncGst = 0m;
        var feeSource = "Not applicable";
        var feeUnavailable = false;
        if (sale.Status == NayaxTransactionStatus.Completed && sale.PaymentType == NayaxPaymentType.Card)
        {
            var rate = EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, sale.AuthorizationDate);
            if (rate is null)
            {
                feeExGst = feeGst = feeIncGst = null;
                feeSource = "Unavailable";
                feeUnavailable = true;
            }
            else
            {
                feeExGst = rate.Value.FeeExGst;
                feeGst = ReportingCalculations.GstFromExcluding(rate.Value.FeeExGst);
                feeIncGst = feeExGst + feeGst;
                feeSource = "Estimated";
            }
        }
        else if (sale.Status == NayaxTransactionStatus.Completed && sale.PaymentType == NayaxPaymentType.Unknown)
        {
            feeExGst = feeGst = feeIncGst = null;
            feeSource = "Unavailable";
            feeUnavailable = true;
        }

        CommissionAgreement? agreement = null;
        var hasOverlappingCommission = false;
        var commissionUnavailable = false;
        if (sale.Status == NayaxTransactionStatus.Completed && sale.SiteId.HasValue)
        {
            var siteAgreements = agreements.Where(x => x.SiteId == sale.SiteId.Value).ToList();
            try
            {
                agreement = EffectiveFinancialConfiguration.ResolveAgreement(
                    siteAgreements.Select(item => new CommissionAgreement(
                        0,
                        item.SiteId,
                        item.EffectiveFrom,
                        item.EffectiveTo,
                        item.CommissionRate,
                        CommissionFrequency.None,
                        item.Basis,
                        null,
                        default,
                        default)),
                    sale.SiteId.Value,
                    sale.AuthorizationDate);
            }
            catch (InvalidOperationException)
            {
                hasOverlappingCommission = true;
            }
            commissionUnavailable = hasOverlappingCommission || agreement is null && siteAgreements.Count > 0;
        }
        else if (sale.Status == NayaxTransactionStatus.Completed)
        {
            commissionUnavailable = true;
        }

        decimal? commissionAmount = agreement is null
            ? (commissionUnavailable ? null : 0m)
            : SiteCommissionCalculator.CommissionAmount(agreement, sale.Sale, sale.PaymentType);

        var isCosted = sale.HasPersistedCost && sale.Status == NayaxTransactionStatus.Completed;
        decimal? grossProfit = isCosted ? sale.Sale - sale.CostOfGoodsSold!.Value : null;
        decimal? directProfit = grossProfit.HasValue && feeIncGst.HasValue && commissionAmount.HasValue
            ? grossProfit.Value - feeIncGst.Value - commissionAmount.Value
            : null;

        return new TransactionRowResult(
            feeExGst, feeGst, feeIncGst, feeSource, feeUnavailable,
            agreement?.CommissionRate, agreement?.Basis, commissionAmount, hasOverlappingCommission, commissionUnavailable,
            isCosted,
            grossProfit, grossProfit.HasValue ? ReportingCalculations.PercentageOf(grossProfit.Value, sale.Sale) : null,
            directProfit, directProfit.HasValue ? ReportingCalculations.PercentageOf(directProfit.Value, sale.Sale) : null);
    }

}
