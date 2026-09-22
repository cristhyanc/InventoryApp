using System.Globalization;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.Reporting;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Domain.Reporting.Transactions;

namespace Inventory.Application.Reporting.Transactions;

/// <summary>
/// The transaction sales report use case: resolves the requested date range/machine scope,
/// retrieves per-transaction facts plus effective fee-rate/commission-agreement data through the
/// narrow <see cref="ITransactionSalesReportFactsProvider"/> port, matches each raw Nayax product
/// identifier/name to the catalogue through the deterministic
/// <c>Inventory.Domain.Reporting.ProductMatching.ProductMatcher</c>, invokes the Domain per-row
/// fee/commission/profit policy and totals policy, then applies status/payment/COGS/search
/// filtering, user-selected sorting, pagination, page-size clamping, and filter-option construction
/// (use-case/presentation concerns, not vending-business rules). Builds the authoritative
/// <see cref="TransactionSalesReportDto"/> consumed by both the API response and the CSV/XLSX
/// export.
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

        var builds = facts.Transactions.Select(detail =>
        {
            var matchedProductId = ProductMatcher.Match(candidates, detail.NayaxProductId, detail.RawProductName);
            var productName = matchedProductId.HasValue
                ? catalogueByName[matchedProductId.Value]
                : string.IsNullOrWhiteSpace(detail.RawProductName) ? "Unmapped product" : ProductMatcher.NormalizeName(detail.RawProductName);

            var result = TransactionRowPolicy.Calculate(
                new TransactionRowInputs(detail.Sale, detail.PaymentType, detail.Status, detail.TransactionDate,
                    detail.SiteId, detail.CostOfGoodsSold, detail.HasPersistedCost),
                facts.FeeRates, facts.CommissionAgreements);

            return new RowBuild(detail, matchedProductId, productName, result);
        }).ToList();

        var options = new TransactionSalesFilterOptionsDto(
            builds.Where(x => x.Detail.SiteId.HasValue).GroupBy(x => x.Detail.SiteId!.Value)
                .Select(x => new TransactionSalesFilterOptionDto(x.Key, x.First().Detail.SiteName ?? $"Site {x.Key}"))
                .OrderBy(x => x.Name).ToList(),
            builds.Where(x => x.ProductId.HasValue).GroupBy(x => x.ProductId!.Value)
                .Select(x => new TransactionSalesFilterOptionDto(x.Key, x.First().ProductName))
                .OrderBy(x => x.Name).ToList());

        var status = FilterValue(filter.Status);
        if (string.IsNullOrWhiteSpace(status)) status = "completed";
        var payment = FilterValue(filter.PaymentType);
        var cogs = FilterValue(filter.CogsStatus);
        var search = filter.Search?.Trim();

        var filtered = builds.Where(x =>
            (!filter.SiteId.HasValue || x.Detail.SiteId == filter.SiteId) &&
            (!filter.ProductId.HasValue || x.ProductId == filter.ProductId) &&
            MatchesPayment(payment, x.Detail.PaymentType) &&
            MatchesStatus(status, x.Detail.Status) &&
            MatchesCogs(cogs, x.Detail.HasPersistedCost) &&
            MatchesSearch(search, x)).ToList();

        var sorted = SortRows(filtered, filter.SortBy, filter.SortDescending).ToList();
        var dtoRows = sorted.Select(ToRowDto).ToList();

        var totalsInputs = filtered.Select(x => new TransactionTotalsRowInputs(
            x.Detail.Status == TransactionSaleStatus.Completed, x.Detail.Sale, x.Detail.PaymentType, x.Result.IsCosted,
            x.Detail.CostOfGoodsSold, x.Result.GrossProfit, x.Result.DirectProfit,
            x.Result.FeeSource == "Estimated", x.Result.FeeExGst ?? 0m, x.Result.FeeGst ?? 0m, x.Result.FeeIncGst ?? 0m,
            x.Result.CommissionAmount ?? 0m)).ToList();
        var totalsResult = TransactionTotalsPolicy.Calculate(totalsInputs);
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
        var missingStatus = filtered.Count(x => x.Detail.TransactionStatusId is null);
        if (missingStatus > 0) notes.Add($"{missingStatus} transaction(s) have no Nayax status ID.");
        if (totals.UncostedCompletedTransactionCount > 0)
            notes.Add($"{totals.UncostedCompletedTransactionCount} completed transaction(s) have incomplete persisted COGS; full profit totals are unavailable.");
        if (filtered.Any(x => x.ProductId is null)) notes.Add("One or more transactions could not be mapped to a catalogue product.");
        if (facts.SiteMappingUnavailable && filtered.Count > 0)
            notes.Add("Current site mapping is unavailable because it comes only from the live Nayax machine CustomerID.");
        if (filtered.Any(x => x.Result.FeeUnavailable))
            notes.Add("An effective-dated estimated card fee is unavailable for one or more completed card transactions.");
        if (filtered.Any(x => x.Result.HasOverlappingCommission))
            notes.Add("Overlapping site commission agreements cover one or more transactions; their direct profit is unavailable.");
        if (filtered.Any(x => x.Result.CommissionUnavailable))
            notes.Add("Site mapping or effective commission agreement coverage is unavailable for one or more completed transactions.");
        if (filtered.Any(x => x.Result.FeeSource == "Estimated"))
            notes.Add("Transaction fees are configured estimates. Imported Nayax fees are period/device-level and are not allocated to transactions.");
        var nayaxCosted = filtered.Count(x => x.Detail.CostSource == "Nayax Historical Export");
        if (nayaxCosted > 0)
            notes.Add($"Historical COGS includes {nayaxCosted} transaction(s) costed from the Nayax transaction export.");
        var quality = new ReportingDataQualityDto(
            MissingStatus: missingStatus > 0,
            HistoricalCostUnavailable: totals.UncostedCompletedTransactionCount > 0,
            GstClassificationMissing: false,
            CommissionNotPersisted: false,
            ContainsUnmappedProducts: filtered.Any(x => x.ProductId is null),
            Notes: notes);

        var pageSize = filter.PageSize is 50 or 100 or 250 ? filter.PageSize : 50;
        var page = Math.Max(1, filter.Page);
        var resultRows = paginate ? dtoRows.Skip((page - 1) * pageSize).Take(pageSize).ToList() : dtoRows;

        return new TransactionSalesReportDto(range.From, range.ToDate, resultRows, totals, quality, page, pageSize, dtoRows.Count, options);
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
