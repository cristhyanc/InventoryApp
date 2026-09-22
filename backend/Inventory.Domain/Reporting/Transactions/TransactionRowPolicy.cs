namespace Inventory.Domain.Reporting.Transactions;

/// <summary>
/// Inputs required to derive a single transaction's estimated Nayax fee, site commission, and
/// gross/direct profit. Already-classified facts for one sale; carries no query or persistence
/// behavior.
/// </summary>
public readonly record struct TransactionRowInputs(
    decimal Sale,
    TransactionPaymentType PaymentType,
    TransactionSaleStatus Status,
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
    TransactionCommissionBasis? CommissionBasis,
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
        IReadOnlyList<EffectiveFeeRate> rates,
        IReadOnlyList<EffectiveCommissionAgreement> agreements)
    {
        decimal? feeExGst = 0m, feeGst = 0m, feeIncGst = 0m;
        var feeSource = "Not applicable";
        var feeUnavailable = false;
        if (sale.Status == TransactionSaleStatus.Completed && sale.PaymentType == TransactionPaymentType.Card)
        {
            var rate = ResolveFeeRate(rates, sale.AuthorizationDate);
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
        else if (sale.Status == TransactionSaleStatus.Completed && sale.PaymentType == TransactionPaymentType.Unknown)
        {
            feeExGst = feeGst = feeIncGst = null;
            feeSource = "Unavailable";
            feeUnavailable = true;
        }

        EffectiveCommissionAgreement? agreement = null;
        var hasOverlappingCommission = false;
        var commissionUnavailable = false;
        if (sale.Status == TransactionSaleStatus.Completed && sale.SiteId.HasValue)
        {
            var siteAgreements = agreements.Where(x => x.SiteId == sale.SiteId.Value).ToList();
            var (resolved, hasOverlap) = ResolveAgreement(siteAgreements, sale.SiteId.Value, sale.AuthorizationDate);
            if (hasOverlap)
            {
                hasOverlappingCommission = true;
                commissionUnavailable = true;
            }
            else
            {
                agreement = resolved;
                commissionUnavailable = agreement is null && siteAgreements.Count > 0;
            }
        }
        else if (sale.Status == TransactionSaleStatus.Completed)
        {
            commissionUnavailable = true;
        }

        decimal? commissionAmount = agreement is null
            ? (commissionUnavailable ? null : 0m)
            : CommissionAmount(agreement.Value, sale.Sale, sale.PaymentType);

        var isCosted = sale.HasPersistedCost && sale.Status == TransactionSaleStatus.Completed;
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

    private static EffectiveFeeRate? ResolveFeeRate(IReadOnlyList<EffectiveFeeRate> rates, DateTime effectiveAt) =>
        rates
            .Where(x => x.EffectiveFrom.Date <= effectiveAt.Date)
            .OrderByDescending(x => x.EffectiveFrom)
            .Select(x => (EffectiveFeeRate?)x)
            .FirstOrDefault();

    // Returns the single matching agreement, or (null, HasOverlap: true) when more than one
    // agreement covers the site on this date, mirroring the prior throwing behavior without using
    // an exception for control flow.
    private static (EffectiveCommissionAgreement? Agreement, bool HasOverlap) ResolveAgreement(
        IReadOnlyList<EffectiveCommissionAgreement> agreements, long siteId, DateTime effectiveAt)
    {
        var matches = agreements.Where(x =>
            x.SiteId == siteId &&
            x.EffectiveFrom.Date <= effectiveAt.Date &&
            (!x.EffectiveTo.HasValue || x.EffectiveTo.Value.Date >= effectiveAt.Date)).ToList();

        return matches.Count switch
        {
            0 => (null, false),
            1 => (matches[0], false),
            _ => (null, true)
        };
    }

    private static decimal EligibleSales(TransactionCommissionBasis basis, decimal sale, TransactionPaymentType paymentType) =>
        basis switch
        {
            TransactionCommissionBasis.CardSales => paymentType == TransactionPaymentType.Card ? sale : 0m,
            TransactionCommissionBasis.SalesExGst => sale - ReportingCalculations.GstFromInclusive(sale),
            _ => sale
        };

    private static decimal CommissionAmount(EffectiveCommissionAgreement agreement, decimal sale, TransactionPaymentType paymentType) =>
        EligibleSales(agreement.Basis, sale, paymentType) * agreement.CommissionRate;
}
