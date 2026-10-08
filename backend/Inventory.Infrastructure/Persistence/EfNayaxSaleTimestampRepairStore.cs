using Inventory.Application.SaleTimestampRepair;
using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

// The Domain and the persistence model deliberately share this enum's name, the same way
// InventoryCostBaselineSource is mirrored; the aliases are what keep the one translation between
// them explicit and unambiguous in this file.
using DomainEvidenceSource = Inventory.Domain.Nayax.NayaxSaleTimestampEvidenceSource;
using StoredEvidenceSource = Inventory.Infrastructure.Models.NayaxSaleTimestampEvidenceSource;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSaleTimestampRepairStore"/> (issue #472). It lives
/// in Inventory.Infrastructure beside <see cref="AppDbContext"/> and the <see cref="NayaxSales"/>,
/// <see cref="NayaxSaleTimestampRepair"/>, <see cref="NayaxSaleTimestampRepairPreviewDraft"/>,
/// <see cref="Product"/> and <see cref="InventoryCostTransitionBaseline"/> persistence models it
/// reads, like every other persistence adapter since issue #309.
///
/// It decides nothing. The load projects raw stored facts, and the write sets exactly the one column
/// a repair has authority over - <see cref="NayaxSales.MachineAuthorizationTime"/> - on the rows the
/// apply itself read inside its own transaction, and appends the audit row that records what moved,
/// from which source and by whom. No amount, status, payment method, product mapping, costing value
/// or stock movement is written, and no sale is added or removed, so a repair can neither double
/// count a transaction nor repeat a physical stock movement.
///
/// Everything it reads and writes goes through <see cref="AppDbContext"/>'s business query filters
/// and its ownership stamp on save, so there is no business predicate here by design and the adapter
/// cannot see, examine or repair another business's sale, nor load another business's stored plan
/// (AGENTS.md § Tenant ownership and data isolation). Completed sales are selected through the one
/// shared <see cref="EfNayaxSalesQueries.CompletedSalePredicate"/> rather than a repeated status
/// comparison. On a relational provider an apply runs in a database transaction; the EF InMemory
/// provider used by some tests has none.
/// </summary>
public sealed class EfNayaxSaleTimestampRepairStore : INayaxSaleTimestampRepairStore
{
    private readonly AppDbContext _db;

    public EfNayaxSaleTimestampRepairStore(AppDbContext db) => _db = db;

    public async Task<INayaxSaleTimestampRepairTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new Transaction(_db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    public async Task<IReadOnlyList<StoredNayaxSale>> ListSalesByTransactionIdAsync(
        IReadOnlyCollection<long> transactionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);

        if (transactionIds.Count == 0)
            return [];

        return (await _db.NayaxSales.AsNoTracking()
                .Where(sale => transactionIds.Contains(sale.TransactionID))
                .OrderBy(sale => sale.MachineAuthorizationTime)
                .ThenBy(sale => sale.TransactionID)
                .ToListAsync(cancellationToken))
            .Select(Project)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredNayaxSale>> ListSalesInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        CancellationToken cancellationToken) =>
        (await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.MachineAuthorizationTime >= fromUtcInclusive
                && sale.MachineAuthorizationTime <= toUtcInclusive)
            .OrderBy(sale => sale.MachineAuthorizationTime)
            .ThenBy(sale => sale.TransactionID)
            .ToListAsync(cancellationToken))
        .Select(Project)
        .ToList();

    public async Task<IReadOnlyList<StoredNayaxSale>> ListCompletedSalesAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken) =>
        (await _db.NayaxSales.AsNoTracking()
            .Where(EfNayaxSalesQueries.CompletedSalePredicate)
            .Where(sale => sale.MachineAuthorizationTime >= fromUtcInclusive
                && sale.MachineAuthorizationTime < toUtcExclusive)
            .OrderBy(sale => sale.MachineAuthorizationTime)
            .ThenBy(sale => sale.TransactionID)
            .ToListAsync(cancellationToken))
        .Select(Project)
        .ToList();

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(
        CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking()
            .Select(product => new { product.Id, product.Name })
            .ToListAsync(cancellationToken))
        .ConvertAll(product => new ProductMatchCandidate(product.Id, product.Name));

    public async Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
            return new Dictionary<long, DateTime>();

        return await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Where(baseline => productIds.Contains(baseline.ProductId))
            .ToDictionaryAsync(baseline => baseline.ProductId, baseline => baseline.CutoffAt, cancellationToken);
    }

    public void AddDraft(NewNayaxSaleTimestampRepairDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _db.NayaxSaleTimestampRepairPreviewDrafts.Add(new NayaxSaleTimestampRepairPreviewDraft
        {
            Id = draft.Id,
            PlanJson = draft.PlanJson,
            CreatedAt = draft.CreatedAt,
            ExpiresAt = draft.ExpiresAt,
        });
    }

    public async Task<StoredNayaxSaleTimestampRepairDraft?> FindDraftAsync(
        Guid previewId,
        CancellationToken cancellationToken)
    {
        // Loaded tracked, so MarkDraftApplied stages the applied marker on the row the apply itself
        // read, inside its transaction.
        var draft = await _db.NayaxSaleTimestampRepairPreviewDrafts
            .SingleOrDefaultAsync(x => x.Id == previewId, cancellationToken);
        return draft is null
            ? null
            : new StoredNayaxSaleTimestampRepairDraft(draft.Id, draft.PlanJson, draft.ExpiresAt, draft.AppliedAt);
    }

    public void MarkDraftApplied(Guid previewId, DateTime appliedAt)
    {
        var draft = _db.NayaxSaleTimestampRepairPreviewDrafts.Local.SingleOrDefault(x => x.Id == previewId)
            ?? throw new InvalidOperationException("The sale timestamp repair preview was not loaded for update.");
        draft.AppliedAt = appliedAt;
    }

    public async Task<IReadOnlyList<NayaxSaleTimestampRepairRecord>> RepairAsync(
        NayaxSaleTimestampRepairApplication application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (application.Changes.Count == 0)
            return [];

        var transactionIds = application.Changes.Select(change => change.TransactionId).ToList();
        var sales = await _db.NayaxSales
            .Where(sale => transactionIds.Contains(sale.TransactionID))
            .ToDictionaryAsync(sale => sale.TransactionID, cancellationToken);

        var audit = new List<NayaxSaleTimestampRepair>(application.Changes.Count);
        foreach (var change in application.Changes)
        {
            // The sale cannot be absent: the apply re-read exactly these transactions inside this
            // transaction and compared them against the plan. An absent one is therefore a
            // fail-closed error that rolls the whole apply back rather than a partial repair.
            if (!sales.TryGetValue(change.TransactionId, out var sale))
                throw new InvalidOperationException(
                    $"The sale a previewed timestamp repair named (transaction {change.TransactionId}) is no "
                        + "longer available; nothing was repaired.");

            sale.MachineAuthorizationTime = change.RepairedInstantUtc;
            audit.Add(new NayaxSaleTimestampRepair
            {
                TransactionId = change.TransactionId,
                MachineId = change.MachineId,
                PreviousInstantUtc = change.PreviousInstantUtc,
                RepairedInstantUtc = change.RepairedInstantUtc,
                PreviousBusinessDate = change.PreviousBusinessDate,
                RepairedBusinessDate = change.RepairedBusinessDate,
                EvidenceSource = (StoredEvidenceSource)change.EvidenceSource,
                EvidenceReference = change.EvidenceReference,
                PreviewId = application.PreviewId,
                AppliedAt = application.AppliedAt,
                AppliedByDirectoryTenantId = application.AppliedByDirectoryTenantId,
                AppliedByObjectId = application.AppliedByObjectId,
            });
        }

        _db.NayaxSaleTimestampRepairs.AddRange(audit);
        await _db.SaveChangesAsync(cancellationToken);
        return audit.ConvertAll(Project);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    private static StoredNayaxSale Project(NayaxSales sale) =>
        new(
            sale.TransactionID,
            sale.MachineID,
            sale.MachineName,
            sale.SettlementValue,
            sale.TransactionStatusId,
            sale.MachineAuthorizationTime,
            sale.NayaxProductId,
            sale.ProductName);

    private static NayaxSaleTimestampRepairRecord Project(NayaxSaleTimestampRepair repair) =>
        new(
            repair.Id,
            repair.TransactionId,
            repair.MachineId,
            repair.PreviousInstantUtc,
            repair.RepairedInstantUtc,
            repair.PreviousBusinessDate,
            repair.RepairedBusinessDate,
            (DomainEvidenceSource)repair.EvidenceSource,
            repair.EvidenceReference,
            repair.PreviewId,
            repair.AppliedAt,
            repair.AppliedByDirectoryTenantId,
            repair.AppliedByObjectId);

    private sealed class Transaction(IDbContextTransaction? transaction) : INayaxSaleTimestampRepairTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
