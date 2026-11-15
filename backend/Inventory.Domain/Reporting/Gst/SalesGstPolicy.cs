using Inventory.Domain.Gst;

namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// One already-aggregated group of a period's completed sales that share one product GST rule
/// (issue #1255). <see cref="ProductGstRule"/> is the rule stored on the product the sales are
/// attributed to, exactly as persisted, and <c>null</c> means the sales' product could not be
/// resolved at all - a real state, because <c>NayaxSales.ProductId</c> is nullable.
///
/// It carries a rule rather than a decision: which rules are taxable is
/// <see cref="SalesGstPolicy"/>'s to say, so a reporting adapter groups rows and decides nothing.
/// </summary>
public readonly record struct SalesByGstRule(GstClassification? ProductGstRule, decimal Sales);

/// <summary>
/// A period's sales split by what each product's GST rule says about it, and the one GST-on-sales
/// figure derived from the taxable part of it.
///
/// <see cref="NoGstRuleSales"/> is a subset of <see cref="TaxableSales"/>, not a fourth bucket
/// beside it: a product nobody has given a rule is taxable, and this figure says how much of the
/// taxable total rests on that default rather than on a configured rule.
/// </summary>
public readonly record struct SalesGstResult(
    decimal TaxableSales,
    decimal GstFreeSales,
    decimal NoGstRuleSales,
    decimal GstOnSales);

/// <summary>
/// The one authoritative GST-on-sales calculation (issue #1255, the repository owner's decisions of
/// 13 and 14 November 2026). Sales are GST-inclusive, so GST on sales is one eleventh of the
/// taxable sales of the period, rounded to cents.
///
/// <para><strong>Only an explicitly GST-free product takes its sales out of the taxable total.</strong>
/// A product whose rule is <see cref="GstClassification.Taxable"/>, a product with no rule at all
/// (<see cref="GstRules.None"/>, which is what every product starts at and most products still
/// carry) and a sale whose product cannot be resolved are all taxable. Treating "no rule" as
/// GST-free would drop GST on sales to almost nothing, so the default stays taxable and the sales
/// that rest on it are reported separately as <see cref="SalesGstResult.NoGstRuleSales"/> instead of
/// disappearing into the total.</para>
///
/// <para><strong>The GST is calculated once, on the total.</strong> One eleventh of the period's
/// taxable sales, rounded to cents away from zero, is deliberately not the sum of a rounded figure
/// per sale: a period of a thousand small sales would otherwise accumulate up to half a cent of
/// drift per sale. This is the opposite choice from
/// <see cref="PurchaseInputGstPolicy"/>/<c>PurchaseGstPolicy</c>, where the authoritative amount is
/// each invoice component's own rounded GST, and both are deliberate: a purchase component is a
/// recorded amount somebody can check against an invoice line, while a sale is one of thousands of
/// rows that only ever add up to a BAS figure.</para>
///
/// <para><strong>The rule is read at report time</strong> and the application tracks no history of
/// it: changing a product's rule changes what every period's report says about the sales of that
/// product. That is stated on the report help, and the alternative - a GST classification persisted
/// per sale - is explicitly not part of this change.</para>
///
/// <para>This policy never infers a rule from an amount, a product name, a category or a price, and
/// nothing here classifies a purchase: purchase input GST stays
/// <see cref="PurchaseInputGstPolicy"/>'s (AGENTS.md § Nayax processing fees and GST).</para>
/// </summary>
public static class SalesGstPolicy
{
    /// <summary>
    /// Whether <paramref name="rule"/> takes a product's sales out of the taxable total. Only an
    /// explicit <see cref="GstClassification.GstFree"/> rule does.
    /// </summary>
    public static bool IsGstFree(GstClassification? rule) => rule == GstClassification.GstFree;

    /// <summary>
    /// Whether <paramref name="rule"/> is no rule at all: an unresolved product (<c>null</c>),
    /// <see cref="GstRules.None"/>, or a stored value outside the declared vocabulary.
    ///
    /// <para>An undeclared value is counted here rather than refused, unlike a <em>submitted</em>
    /// one (<see cref="GstClassifications.Require"/>): a report reads what is already stored, and
    /// failing the whole report - or quietly calling such a product taxable with a rule - would be
    /// worse than reporting it as a product whose rule nobody has established.</para>
    /// </summary>
    public static bool HasNoRule(GstClassification? rule) =>
        rule is not { } value || value == GstRules.None || !GstClassifications.IsSupported(value);

    /// <summary>
    /// The period's taxable, GST-free and no-rule sales, and the GST on the taxable part.
    /// </summary>
    /// <param name="salesByRule">
    /// The period's completed sales, already aggregated per stored product GST rule. Several groups
    /// may carry the same rule; they are added together.
    /// </param>
    public static SalesGstResult Calculate(IEnumerable<SalesByGstRule> salesByRule)
    {
        ArgumentNullException.ThrowIfNull(salesByRule);

        var taxableSales = 0m;
        var gstFreeSales = 0m;
        var noRuleSales = 0m;

        foreach (var group in salesByRule)
        {
            if (IsGstFree(group.ProductGstRule))
            {
                gstFreeSales += group.Sales;
                continue;
            }

            taxableSales += group.Sales;
            if (HasNoRule(group.ProductGstRule))
                noRuleSales += group.Sales;
        }

        return new SalesGstResult(taxableSales, gstFreeSales, noRuleSales, GstOnTaxableSales(taxableSales));
    }

    /// <summary>
    /// One eleventh of a GST-inclusive taxable sales total, rounded to cents away from zero - the
    /// ordinary currency rounding, not the banker's rounding <see cref="Math.Round(decimal, int)"/>
    /// defaults to. The division itself is <c>ReportingCalculations.GstFromInclusive</c>, the one
    /// GST-inclusive formula in the Domain.
    /// </summary>
    private static decimal GstOnTaxableSales(decimal taxableSales) =>
        Math.Round(ReportingCalculations.GstFromInclusive(taxableSales), 2, MidpointRounding.AwayFromZero);
}
