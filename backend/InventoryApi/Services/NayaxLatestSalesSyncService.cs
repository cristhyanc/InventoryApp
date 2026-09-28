using Inventory.Application.Nayax;
using InventoryApi.Data;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class NayaxLatestSalesSyncService : INayaxLatestSalesSyncService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly ISaleCostingService _saleCosting;
    private readonly IInventoryCostRebuildService _inventoryCostRebuild;

    public NayaxLatestSalesSyncService(
        AppDbContext db,
        INayaxLynxClient nayaxLynxClient,
        ISaleCostingService? saleCosting = null,
        IInventoryCostRebuildService? inventoryCostRebuild = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
        _saleCosting = saleCosting ?? new SaleCostingService(db);
        _inventoryCostRebuild = inventoryCostRebuild ?? new InventoryCostRebuildService(db);
    }

    public async Task SyncLatestSalesAsync(CancellationToken ct = default)
    {
        var machineIds = (await _nayaxLynxClient.GetMachinesAsync(ct))
            .Select(x => x.MachineID)
            .ToList();

        var products = await _db.Products.AsNoTracking().ToListAsync(ct);
        var affected = new Dictionary<long, DateTime>();
        foreach (var machineId in machineIds)
        {
            var sales = await _nayaxLynxClient.GetMachineLastSalesAsync(machineId, ct);
            foreach (var sale in sales)
            {
                var matchedProduct = NayaxProductMatcher.Match(
                    products,
                    sale.NayaxProductId,
                    sale.ProductName);
                var existing = await _db.NayaxSales
                    .FirstOrDefaultAsync(x => x.TransactionID == sale.TransactionID, ct);
                if (existing is null)
                {
                    var added = new NayaxSales
                    {
                        TransactionID = sale.TransactionID,
                        TransactionStatusId = sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250,
                        MachineID = sale.MachineID,
                        NayaxProductId = matchedProduct?.Id ?? sale.NayaxProductId,
                        MachineName = sale.MachineName,
                        SettlementValue = sale.SettlementValue,
                        PaymentMethod = sale.PaymentMethod,
                        ProductName = sale.ProductName,
                        NayaxProductCostPrice = sale.ProductCostPrice,
                        MachineAuthorizationTime = sale.MachineAuthorizationTime
                    };
                    _db.NayaxSales.Add(added);
                    await _saleCosting.CostSaleAsync(added, cancellationToken: ct);
                    if (NayaxTransactionStatusClassifier.IsCompletedSale(added))
                    {
                        if (matchedProduct is not null &&
                            (!affected.TryGetValue(matchedProduct.Id, out var existingAt) || added.MachineAuthorizationTime < existingAt))
                            affected[matchedProduct.Id] = added.MachineAuthorizationTime;
                    }
                    continue;
                }

                var enriched = false;
                if (!existing.NayaxProductId.HasValue)
                {
                    var existingMatch = NayaxProductMatcher.Match(
                        products,
                        sale.NayaxProductId,
                        sale.ProductName ?? existing.ProductName);
                    if (existingMatch is not null)
                    {
                        existing.NayaxProductId = existingMatch.Id;
                        matchedProduct = existingMatch;
                        enriched = true;
                    }
                }
                if (!existing.TransactionStatusId.HasValue)
                {
                    existing.TransactionStatusId =
                        sale.SettlementValue > 0 ? NayaxTransactionStatusIds.Completed : NayaxTransactionStatusIds.CancelledOrDeclined250;
                    enriched = true;
                }
                if (enriched)
                {
                    await _saleCosting.CostSaleAsync(
                        existing,
                        cancellationToken: ct);
                    if (NayaxTransactionStatusClassifier.IsCompletedSale(existing))
                    {
                        matchedProduct ??= NayaxProductMatcher.Match(
                            products, existing.NayaxProductId, existing.ProductName);
                        if (matchedProduct is not null &&
                            (!affected.TryGetValue(matchedProduct.Id, out var existingAt) ||
                             existing.MachineAuthorizationTime < existingAt))
                            affected[matchedProduct.Id] = existing.MachineAuthorizationTime;
                    }
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        if (affected.Count == 0)
            return;
        var baselines = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(x => affected.Keys.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.CutoffAt, ct);
        foreach (var item in affected)
            if (baselines.TryGetValue(item.Key, out var cutoff) && item.Value > cutoff)
                await _inventoryCostRebuild.RebuildAsync(item.Key, item.Value, cancellationToken: ct);
        await _db.SaveChangesAsync(ct);
    }
}
