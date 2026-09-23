using System.Globalization;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Domain.Reporting.Transactions;

namespace Inventory.Application.Reporting.Transactions;

/// <summary>
/// The transaction sales report use case: resolves the requested date range/machine scope, then
/// streams per-transaction facts plus effective fee-rate/commission-agreement data through the
/// narrow <see cref="ITransactionSalesReportFactsProvider"/> port, matching each raw Nayax product
/// identifier/name to the catalogue through the deterministic
/// <c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c>, invoking the Domain per-row
/// fee/commission/profit policy, applying status/payment/COGS/search filtering, and accumulating
/// totals, filter options, and quality facts in one pass over the stream (use-case/presentation
/// concerns, not vending-business rules). Totals and filter options always cover the complete
/// date/machine scope; for a paginated request, only the <c>page * pageSize</c> best-sorted filtered
/// row candidates needed to answer that page are retained, via <see cref="BoundedTopSelector{T}"/>.
/// An unpaginated (export) request intentionally retains every filtered row, since the complete
/// result set is the answer. Builds the authoritative <see cref="TransactionSalesReportDto"/>
/// consumed by both the API response and the CSV/XLSX export.
/// </summary>
public sealed class GetTransactionSalesReport
{
    private readonly ITransactionSalesReportFactsProvider _facts;

    public GetTransactionSalesReport(ITransactionSalesReportFactsProvider facts)
    {
        _facts = facts;
    }

    public Task<TransactionSalesReportDto> Handle(TransactionSalesFilterDto filter, CancellationToken cancellationToken) =>
        Handle(filter, paginate: true, cancellationToken);

    public async Task<TransactionSalesReportDto> Handle(TransactionSalesFilterDto filter, bool paginate, CancellationToken cancellationToken)
    {
        var range = new DateRange((filter.From ?? new DateTime(1900, 1, 1)).Date, (filter.To ?? new DateTime(9999, 12, 30)).Date);
        if (range.ToDate < range.From) range = new DateRange(range.ToDate, range.From);

        var facts = await _facts.GetFactsAsync(range.From, range.ToDate, filter.MachineId, cancellationToken);
        var candidates = facts.ProductCatalogue.Select(x => new ProductMatchCandidate(x.Id, x.Name)).ToArray();
        var catalogueByName = facts.ProductCatalogue.ToDictionary(x => x.Id, x => x.Name);

        var status = FilterValue(filter.Status);
        if (string.IsNullOrWhiteSpace(status)) status = "completed";
        var payment = FilterValue(filter.PaymentType);
        var cogs = FilterValue(filter.CogsStatus);
        var search = filter.Search?.Trim();

        var pageSize = filter.PageSize is 50 or 100 or 250 ? filter.PageSize : 50;
        var page = Math.Max(1, filter.Page);

        bool MatchesFilters(RowBuild build) =>
            (!filter.SiteId.HasValue || build.Detail.SiteId == filter.SiteId) &&
            (!filter.ProductId.HasValue || build.ProductId == filter.ProductId) &&
            MatchesPayment(payment, build.Detail.PaymentType) &&
            MatchesStatus(status, build.Detail.Status) &&
            MatchesCogs(cogs, build.Detail.HasPersistedCost) &&
            MatchesSearch(search, build);

        // Distinct site/product option state, keyed by identity: first-seen name wins, matching the
        // prior GroupBy(...).First() semantics, since the stream preserves the same underlying query
        // order the prior in-memory list did.
        var siteOptions = new Dictionary<long, string>();
        var productOptions = new Dictionary<long, string>();

        var totalsAccumulator = new TransactionTotalsAccumulator();
        var filteredCount = 0;
        var missingStatusCount = 0;
        var containsUnmappedProducts = false;
        var anyFeeUnavailable = false;
        var anyOverlappingCommission = false;
        var anyCommissionUnavailable = false;
        var anyEstimatedFee = false;
        var nayaxCostedCount = 0;

        BoundedTopSelector<RowBuild>? bounded = null;
        List<RowBuild>? unboundedFiltered = null;
        if (paginate)
        {
            var capacity = (int)Math.Min((long)page * pageSize, int.MaxValue);
            bounded = new BoundedTopSelector<RowBuild>(BuildComparer(filter.SortBy, filter.SortDescending), capacity);
        }
        else
        {
            unboundedFiltered = [];
        }

        await foreach (var detail in facts.Transactions.WithCancellation(cancellationToken))
        {
            var matchedProductId = ProductMatcher.Match(candidates, detail.NayaxProductId, detail.RawProductName);
            var productName = matchedProductId.HasValue
                ? catalogueByName[matchedProductId.Value]
                : string.IsNullOrWhiteSpace(detail.RawProductName) ? "Unmapped product" : ProductMatcher.NormalizeName(detail.RawProductName);

            var result = TransactionRowPolicy.Calculate(
                new TransactionRowInputs(detail.Sale, detail.PaymentType, detail.Status, detail.TransactionDate,
                    detail.SiteId, detail.CostOfGoodsSold, detail.HasPersistedCost),
                facts.FeeRates, facts.CommissionAgreements);

            var build = new RowBuild(detail, matchedProductId, productName, result);

            if (detail.SiteId.HasValue) siteOptions.TryAdd(detail.SiteId.Value, detail.SiteName ?? $"Site {detail.SiteId.Value}");
            if (matchedProductId.HasValue) productOptions.TryAdd(matchedProductId.Value, productName);

            if (!MatchesFilters(build)) continue;

            filteredCount++;
            totalsAccumulator.Add(new TransactionTotalsRowInputs(
                detail.Status == TransactionSaleStatus.Completed, detail.Sale, detail.PaymentType, result.IsCosted,
                detail.CostOfGoodsSold, result.GrossProfit, result.DirectProfit,
                result.FeeSource == "Estimated", result.FeeExGst ?? 0m, result.FeeGst ?? 0m, result.FeeIncGst ?? 0m,
                result.CommissionAmount ?? 0m));

            if (detail.TransactionStatusId is null) missingStatusCount++;
            if (matchedProductId is null) containsUnmappedProducts = true;
            if (result.FeeUnavailable) anyFeeUnavailable = true;
            if (result.HasOverlappingCommission) anyOverlappingCommission = true;
            if (result.CommissionUnavailable) anyCommissionUnavailable = true;
            if (result.FeeSource == "Estimated") anyEstimatedFee = true;
            if (detail.CostSource == "Nayax Historical Export") nayaxCostedCount++;

            if (paginate) bounded!.Add(build);
            else unboundedFiltered!.Add(build);
        }

        var options = new TransactionSalesFilterOptionsDto(
            siteOptions.Select(x => new TransactionSalesFilterOptionDto(x.Key, x.Value)).OrderBy(x => x.Name).ToList(),
            productOptions.Select(x => new TransactionSalesFilterOptionDto(x.Key, x.Value)).OrderBy(x => x.Name).ToList());

        var totalsResult = totalsAccumulator.ToResult();
        var totals = new TransactionSalesTotalsDto(
            totalsResult.TransactionCount, totalsResult.CompletedTransactionCount, totalsResult.Sales,
            totalsResult.CardSales, totalsResult.CashSales,
            totalsResult.CostedCompletedTransactionCount, totalsResult.UncostedCompletedTransactionCount,
            totalsResult.IsCogsComplete, totalsResult.CostOfGoods, totalsResult.PartialCostOfGoods,
            totalsResult.GrossProfit, totalsResult.GrossMarginPercent, totalsResult.DirectProfit, totalsResult.DirectMarginPercent,
            totalsResult.PartialGrossProfit, totalsResult.PartialDirectProfit,
            totalsResult.EstimatedFeeExGst, totalsResult.EstimatedFeeGst, totalsResult.EstimatedFeeIncGst,
            totalsResult.CommissionAmount);

        var notes = new List<string>();
        if (missingStatusCount > 0) notes.Add($"{missingStatusCount} transaction(s) have no Nayax status ID.");
        if (totals.UncostedCompletedTransactionCount > 0)
            notes.Add($"{totals.UncostedCompletedTransactionCount} completed transaction(s) have incomplete persisted COGS; full profit totals are unavailable.");
        if (containsUnmappedProducts) notes.Add("One or more transactions could not be mapped to a catalogue product.");
        if (facts.SiteMappingUnavailable && filteredCount > 0)
            notes.Add("Current site mapping is unavailable because it comes only from the live Nayax machine CustomerID.");
        if (anyFeeUnavailable)
            notes.Add("An effective-dated estimated card fee is unavailable for one or more completed card transactions.");
        if (anyOverlappingCommission)
            notes.Add("Overlapping site commission agreements cover one or more transactions; their direct profit is unavailable.");
        if (anyCommissionUnavailable)
            notes.Add("Site mapping or effective commission agreement coverage is unavailable for one or more completed transactions.");
        if (anyEstimatedFee)
            notes.Add("Transaction fees are configured estimates. Imported Nayax fees are period/device-level and are not allocated to transactions.");
        if (nayaxCostedCount > 0)
            notes.Add($"Historical COGS includes {nayaxCostedCount} transaction(s) costed from the Nayax transaction export.");
        var quality = new ReportingDataQualityDto(
            MissingStatus: missingStatusCount > 0,
            HistoricalCostUnavailable: totals.UncostedCompletedTransactionCount > 0,
            GstClassificationMissing: false,
            CommissionNotPersisted: false,
            ContainsUnmappedProducts: containsUnmappedProducts,
            Notes: notes);

        List<TransactionSalesRowDto> resultRows;
        if (paginate)
            resultRows = bounded!.Items.Skip((page - 1) * pageSize).Take(pageSize).Select(ToRowDto).ToList();
        else
            resultRows = SortRows(unboundedFiltered!, filter.SortBy, filter.SortDescending).Select(ToRowDto).ToList();

        return new TransactionSalesReportDto(range.From, range.ToDate, resultRows, totals, quality, page, pageSize, filteredCount, options);
    }

    private static TransactionSalesRowDto ToRowDto(RowBuild build)
    {
        var detail = build.Detail;
        var result = build.Result;
        return new TransactionSalesRowDto(
            detail.TransactionDate, detail.TransactionId, detail.MachineId, DisplayMachineName(detail),
            detail.SiteId, detail.SiteName, build.ProductId, build.ProductName,
            detail.PaymentType.ToString(), detail.RawPaymentMethod, detail.Sale,
            detail.NayaxProductCostPrice, detail.UnitCostAtSale, detail.CostOfGoodsSold,
            detail.CostingStatus, detail.CostSource,
            result.GrossProfit, result.GrossMarginPercent, result.DirectProfit, result.DirectMarginPercent,
            result.FeeExGst, result.FeeGst, result.FeeIncGst, result.FeeSource,
            result.CommissionRate, result.CommissionBasis?.ToString(), result.CommissionAmount,
            detail.TransactionStatusId, detail.TransactionStatusDescription,
            detail.Status == TransactionSaleStatus.Completed);
    }

    private static string DisplayMachineName(TransactionSalesReportFactsRow detail) =>
        detail.MachineName ?? $"Machine {detail.MachineId}";

    private static IEnumerable<RowBuild> SortRows(IEnumerable<RowBuild> rows, string? sortBy, bool descending)
    {
        var key = FilterValue(sortBy);
        return (key, descending) switch
        {
            ("machine", true) => rows.OrderByDescending(x => DisplayMachineName(x.Detail)).ThenByDescending(x => x.Detail.TransactionId),
            ("machine", false) => rows.OrderBy(x => DisplayMachineName(x.Detail)).ThenBy(x => x.Detail.TransactionId),
            ("product", true) => rows.OrderByDescending(x => x.ProductName).ThenByDescending(x => x.Detail.TransactionId),
            ("product", false) => rows.OrderBy(x => x.ProductName).ThenBy(x => x.Detail.TransactionId),
            ("sale", true) => rows.OrderByDescending(x => x.Detail.Sale).ThenByDescending(x => x.Detail.TransactionId),
            ("sale", false) => rows.OrderBy(x => x.Detail.Sale).ThenBy(x => x.Detail.TransactionId),
            ("cogs", true) => rows.OrderByDescending(x => x.Detail.CostOfGoodsSold).ThenByDescending(x => x.Detail.TransactionId),
            ("cogs", false) => rows.OrderBy(x => x.Detail.CostOfGoodsSold).ThenBy(x => x.Detail.TransactionId),
            ("gross" or "grossprofit", true) => rows.OrderByDescending(x => x.Result.GrossProfit).ThenByDescending(x => x.Detail.TransactionId),
            ("gross" or "grossprofit", false) => rows.OrderBy(x => x.Result.GrossProfit).ThenBy(x => x.Detail.TransactionId),
            ("direct" or "directprofit", true) => rows.OrderByDescending(x => x.Result.DirectProfit).ThenByDescending(x => x.Detail.TransactionId),
            ("direct" or "directprofit", false) => rows.OrderBy(x => x.Result.DirectProfit).ThenBy(x => x.Detail.TransactionId),
            ("status", true) => rows.OrderByDescending(x => x.Detail.TransactionStatusDescription).ThenByDescending(x => x.Detail.TransactionId),
            ("status", false) => rows.OrderBy(x => x.Detail.TransactionStatusDescription).ThenBy(x => x.Detail.TransactionId),
            (_, false) => rows.OrderBy(x => x.Detail.TransactionDate).ThenBy(x => x.Detail.TransactionId),
            _ => rows.OrderByDescending(x => x.Detail.TransactionDate).ThenByDescending(x => x.Detail.TransactionId)
        };
    }

    // Equivalent per-pair ordering to SortRows's OrderBy/OrderByDescending().ThenBy/ThenByDescending()
    // chains, for feeding BoundedTopSelector<RowBuild> one row at a time instead of sorting a
    // materialised list. Must stay in sync with SortRows: the export (paginate: false) path keeps
    // using SortRows directly on its complete retained list, so both paths are covered by the same
    // regression tests.
    private static IComparer<RowBuild> BuildComparer(string? sortBy, bool descending)
    {
        var key = FilterValue(sortBy);
        Comparison<RowBuild> comparison = key switch
        {
            "machine" => (a, b) => CompareWithTieBreak(DisplayMachineName(a.Detail), DisplayMachineName(b.Detail), a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "product" => (a, b) => CompareWithTieBreak(a.ProductName, b.ProductName, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "sale" => (a, b) => CompareWithTieBreak(a.Detail.Sale, b.Detail.Sale, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "cogs" => (a, b) => CompareWithTieBreak(a.Detail.CostOfGoodsSold, b.Detail.CostOfGoodsSold, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "gross" or "grossprofit" => (a, b) => CompareWithTieBreak(a.Result.GrossProfit, b.Result.GrossProfit, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "direct" or "directprofit" => (a, b) => CompareWithTieBreak(a.Result.DirectProfit, b.Result.DirectProfit, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            "status" => (a, b) => CompareWithTieBreak(a.Detail.TransactionStatusDescription, b.Detail.TransactionStatusDescription, a.Detail.TransactionId, b.Detail.TransactionId, descending),
            _ => (a, b) => CompareWithTieBreak(a.Detail.TransactionDate, b.Detail.TransactionDate, a.Detail.TransactionId, b.Detail.TransactionId, descending)
        };
        return Comparer<RowBuild>.Create(comparison);
    }

    private static int CompareWithTieBreak<TKey>(TKey a, TKey b, long tieA, long tieB, bool descending)
    {
        var cmp = Comparer<TKey>.Default.Compare(a, b);
        if (cmp != 0) return descending ? -cmp : cmp;
        var tie = tieA.CompareTo(tieB);
        return descending ? -tie : tie;
    }

    private static bool MatchesPayment(string value, TransactionPaymentType paymentType) =>
        value is "" or "all" || value == FilterValue(paymentType.ToString());

    private static bool MatchesStatus(string value, TransactionSaleStatus status) =>
        value is "" or "all" || value == FilterValue(status.ToString()) ||
        (value is "cancelled" or "declined" && status == TransactionSaleStatus.CancelledOrDeclined);

    private static bool MatchesCogs(string value, bool hasPersistedCost) =>
        value is "" or "all" || (value == "costed" && hasPersistedCost) ||
        (value is "uncosted" or "incomplete" && !hasPersistedCost);

    private static bool MatchesSearch(string? search, RowBuild build)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var detail = build.Detail;
        return detail.TransactionId.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
            detail.MachineId.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (detail.MachineName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            build.ProductName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (detail.SiteName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (detail.RawPaymentMethod?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string FilterValue(string? value) =>
        value?.Trim().Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant() ?? string.Empty;

    private sealed record RowBuild(TransactionSalesReportFactsRow Detail, long? ProductId, string ProductName, TransactionRowResult Result);
}
