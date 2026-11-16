using Inventory.Application.NayaxSaleReview;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Applies the financial decision to the Nayax sales review list that
/// <c>GET api/nayax-sale-review</c> serializes (issue #1306, audit finding AI-02).
///
/// <para><strong>This is where the financial decision is applied, on the server.</strong> The page
/// behind the endpoint exists so that whoever looks after the machines can see which sales need
/// attention, so it stays open to every member with <c>Dashboard.View</c> - an Operator included -
/// and what changes is the shape of the response, exactly as issue #698's machine figures and issue
/// #768's sale amounts are handled. A caller without <c>Dashboard.ViewFinancials</c> is given every
/// sale, its state, whether it is counted, which of Nayax's own fields disagreed, the decision
/// history and the decisions the table allows, and <c>null</c> for all three money figures the
/// response carries: the stored sale's settled amount, the amount Nayax's own record carried, and
/// the reimbursement difference a decision moved. Never <c>0</c>: that is
/// <see cref="FinancialFigureVisibilityResolution.Withhold"/>'s one job, shared with every other
/// mapper that withholds an amount, so the never-zero rule is written once.</para>
///
/// <para>The summary (<c>GET api/nayax-sale-review/summary</c>) has no projection here because it
/// carries no amount at all: it is two counts of sales, which every role that may read the list may
/// read.</para>
///
/// <para>The projection has to state which visibility it wants, so a new call site cannot publish
/// the amounts by forgetting to ask - and because
/// <see cref="FinancialFigureVisibility.Redacted"/> is the enum's zero, a projection reached
/// without a decision fails closed.</para>
/// </summary>
public static class NayaxSaleReviewResponseMapper
{
    public static NayaxSaleReviewPage ToResponse(NayaxSaleReviewPage page, FinancialFigureVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page with { Items = [.. page.Items.Select(item => ToResponse(item, visibility))] };
    }

    private static NayaxSaleReviewItem ToResponse(NayaxSaleReviewItem item, FinancialFigureVisibility visibility) =>
        item with
        {
            SettlementValue = visibility.Withhold(item.SettlementValue),
            LatestComparison = ToResponse(item.LatestComparison, visibility),
            LatestAbsence = ToResponse(item.LatestAbsence, visibility),
            History = [.. item.History.Select(entry => ToResponse(entry, visibility))],
        };

    /// <summary>
    /// The evidence row with the provider's own amount withheld. Everything else it carries - the
    /// machine, the instant, the product, each field's answer and what <c>lastSales</c> returned for
    /// the machine - stays, because that is what says why the sale is on the page.
    /// </summary>
    private static NayaxSaleReviewEvidence? ToResponse(
        NayaxSaleReviewEvidence? evidence, FinancialFigureVisibility visibility) =>
        evidence is null
            ? null
            : evidence with { ProviderSettlementValue = visibility.Withhold(evidence.ProviderSettlementValue) };

    /// <summary>
    /// One history entry with the reimbursement difference withheld. The period it moved is not an
    /// amount and stays: it says which reconciled period a decision affected, never by how much.
    /// </summary>
    private static NayaxSaleReviewHistoryEntry ToResponse(
        NayaxSaleReviewHistoryEntry entry, FinancialFigureVisibility visibility) =>
        entry with { ReconciledPeriodDifference = visibility.Withhold(entry.ReconciledPeriodDifference) };
}
