using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting.Transactions;

namespace InventoryApi.Tests.Application.Reporting.Transactions;

/// <summary>
/// In-memory fake of the report facts port, so the transaction sales report use case's product
/// matching, filtering, sorting, pagination, and quality-note assembly can be tested without EF
/// Core, SQLite, or the Nayax live machine client.
/// </summary>
public sealed class FakeTransactionSalesReportFactsProvider : ITransactionSalesReportFactsProvider
{
    private readonly TransactionSalesReportFacts _facts;

    public FakeTransactionSalesReportFactsProvider(TransactionSalesReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<TransactionSalesReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static TransactionSalesReportFacts Empty() => new(
        Transactions: [],
        ProductCatalogue: [],
        FeeRates: [],
        CommissionAgreements: [],
        SiteMappingUnavailable: false);

    public static TransactionSalesReportFactsRow CompletedCardRow(
        long transactionId = 1,
        long machineId = 10,
        string machineName = "Machine A",
        long? siteId = null,
        string siteName = null,
        long? nayaxProductId = 1,
        string rawProductName = "Water",
        decimal sale = 10m,
        decimal? costOfGoodsSold = 4m,
        bool hasPersistedCost = true,
        DateTime? transactionDate = null) => new(
        transactionId, transactionDate ?? new DateTime(2025, 8, 1), machineId, machineName,
        siteId, siteName, nayaxProductId, rawProductName,
        TransactionPaymentType.Card, "Credit Card", sale,
        null, costOfGoodsSold, costOfGoodsSold,
        "Costed", "Inventory Ledger", hasPersistedCost,
        TransactionSaleStatus.Completed, 12, "Approved / Completed");
}
