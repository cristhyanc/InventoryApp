using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.Stock;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// A product retired <em>between</em> a stock write's retirement check and that write (issue #1249,
/// audit #1222 finding INV-14).
///
/// <para>Refusing a retired product is a check-then-write rule, and a check-then-write rule whose
/// read happens outside the write's own lock is not a rule: the purchase create read "none of these
/// products is retired" before it stored the document and before
/// <see cref="EfPurchaseStore.CreateAsync"/> opened its transaction, and the manual adjustment read
/// the product's active flag before <see cref="EfStockAdjustmentStore.ApplyAsync"/> took
/// <c>BEGIN IMMEDIATE</c>. A retirement committed in that window was therefore followed by exactly
/// the receipt, restock movement and cost rebuild - or the adjustment and its costing - that the
/// rule exists to prevent. These tests close that window by asserting the <em>mutation itself</em>
/// refuses, with the named message, and that a fresh context sees nothing written.</para>
///
/// <para>The interleaving is made deterministic rather than hoped for: each use case runs over the
/// real adapter wrapped in a store that commits the retirement - from a separate context on its own
/// connection - at the exact moment the early check has answered "active". It runs against a real
/// SQLite <strong>file</strong> with one connection per writer, the same shape as
/// <see cref="PurchaseCreateConcurrencyTests"/> and
/// <see cref="ManualStockAdjustmentConcurrencyTests"/>, because a shared in-memory database is one
/// connection and the EF InMemory provider has no locking at all.</para>
/// </summary>
public sealed class RetiredProductWriteRaceTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const string ProductName = "Coke Zero";
    private const int OpeningStock = 20;
    private const decimal OpeningUnitCost = 2m;

    private static readonly DateTime PurchaseDate = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-retired-writes-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public RetiredProductWriteRaceTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = PurchaseDate,
        });
        db.SaveChanges();
        SeedActiveProduct();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The purchase-create interleaving, on the path that carries no key, no document and no
    /// supplier-order completion - the one that used to choose a deferred transaction, and so the one
    /// with the widest window. The create must be refused by name, and nothing of it may survive:
    /// no purchase, no line, no restock movement, and neither the shelf quantity nor the costing
    /// position may move.
    /// </summary>
    [Fact]
    public async Task A_purchase_create_is_refused_when_the_product_is_retired_after_its_preflight()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var useCase = TestPurchaseUseCases.Upload(
            RetiringAfterTheCheck(RealPurchaseStore(db)),
            new RecordingDocumentStorage(),
            new FakeClock(PurchaseDate));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.Handle(
            null,
            Fields(),
            [new PurchaseItemInput(ProductId, 4m, 1.10m)],
            CancellationToken.None));

        Assert.Equal(PurchaseRetiredProductPolicy.NewLineRefusalMessage(ProductName), refusal.Message);
        AssertNothingWasWritten();
    }

    /// <summary>
    /// The same refusal with a document and an enforced allowance: the bytes this request stored and
    /// the storage it reserved for them must both be gone, so a refused create leaves no orphan.
    /// </summary>
    [Fact]
    public async Task A_purchase_create_refused_for_a_retirement_race_leaves_no_document_and_no_reservation()
    {
        var documents = new RecordingDocumentStorage();
        var quotaStore = new FakeAttachmentQuotaStore();

        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var useCase = TestPurchaseUseCases.Upload(
            RetiringAfterTheCheck(RealPurchaseStore(db)),
            documents,
            new FakeClock(PurchaseDate),
            quota: FakeAttachmentQuota.Enforcing(quotaStore));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.Handle(
            new PurchaseFileInput("receipt.jpg", "image/jpeg", 3, () => new MemoryStream([1, 2, 3])),
            Fields(),
            [new PurchaseItemInput(ProductId, 4m, 1.10m)],
            CancellationToken.None));

        Assert.Equal(PurchaseRetiredProductPolicy.NewLineRefusalMessage(ProductName), refusal.Message);
        AssertNothingWasWritten();

        // The document was written before the write transaction opened, so the refusal has to take it
        // away again - and the reservation with it.
        var stored = Assert.Single(documents.SavedFileNames);
        Assert.Equal(stored, Assert.Single(documents.DeletedFileNames));
        Assert.False(documents.Holds(stored));
        Assert.Single(quotaStore.Requests);
        Assert.Empty(quotaStore.Outstanding);
    }

    /// <summary>
    /// A purchase that was already recorded under its idempotency key still replays, with no new
    /// stock, even once the product has been retired: the replay answers before the refusal is
    /// reached, because the retry is not a new purchase of a retired product - it is the purchase
    /// that was already saved while the product was active.
    /// </summary>
    [Fact]
    public async Task A_recorded_purchase_still_replays_under_its_key_after_the_product_is_retired()
    {
        var claim = new PurchaseIdempotencyClaim(
            "7a0d1a9e-1f8c-4a5f-8a1f-2b3c4d5e6f70", "fingerprint-of-the-one-submission");

        int savedId;
        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            var saved = await RealPurchaseStore(db).CreateAsync(
                Fields(),
                [new PurchaseItemInput(ProductId, 4m, 1.10m)],
                file: null,
                supplierOrderCompletion: null,
                attachmentReservation: null,
                claim,
                CancellationToken.None);
            Assert.False(saved.Replayed);
            savedId = saved.Purchase.Id;
        }

        Retire();

        await using (var db = TestAppDbContext.For(Options(), BusinessA))
        {
            var replayed = await RealPurchaseStore(db).CreateAsync(
                Fields(),
                [new PurchaseItemInput(ProductId, 4m, 1.10m)],
                file: null,
                supplierOrderCompletion: null,
                attachmentReservation: null,
                claim,
                CancellationToken.None);

            Assert.True(replayed.Replayed);
            Assert.Equal(savedId, replayed.Purchase.Id);
        }

        var state = Read();
        Assert.Equal(1, state.PurchaseCount);
        Assert.Equal(1, state.PurchaseMovementCount);
        Assert.Equal(OpeningStock + 4, state.QuantityInStock);
    }

    /// <summary>
    /// The manual-adjustment interleaving: the use case reads the product's active flag, the
    /// retirement commits, and the apply must then refuse with the adjustment's own Domain message
    /// rather than record a movement, move the shelf quantity or rebuild the cost.
    /// </summary>
    [Fact]
    public async Task A_manual_adjustment_is_refused_when_the_product_is_retired_after_its_preflight()
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        var store = new RetiringAfterTheCheckStockAdjustmentStore(
            new EfStockAdjustmentStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            Retire);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new AdjustStock(store, new EfLocalMachineStore(db)).Handle(
                ProductId,
                new ManualStockAdjustmentInput(
                    -4, DomainStock.StockAdjustmentReason.Damaged, "note", null, null, null),
                CancellationToken.None));

        Assert.Equal(DomainStock.ManualStockAdjustmentPolicy.RetiredProductMessage(ProductName), refusal.Message);
        AssertNothingWasWritten();
    }

    private static PurchaseFields Fields() =>
        new("Weekly restock", null, 30m, null, null, PurchaseDate, null, null, null, "INV-1");

    private EfPurchaseStore RealPurchaseStore(AppDbContext db) =>
        new(db, TestCostingUseCases.Rebuild(db), new FakeClock(PurchaseDate));

    private RetiringAfterTheCheckPurchaseStore RetiringAfterTheCheck(IPurchaseStore inner) =>
        new(inner, Retire);

    /// <summary>
    /// Commits the retirement the way a competing request would: its own context, on its own
    /// connection, outside whatever transaction the racing write is about to open.
    /// </summary>
    private void Retire()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        var product = db.Products.Single(candidate => candidate.Id == ProductId);
        product.IsActive = false;
        db.SaveChanges();
    }

    /// <summary>
    /// Seeds the product active, with the opening costed Restock and the costing position it leaves,
    /// so "nothing changed" is an assertion about real figures rather than about zeroes.
    /// </summary>
    private void SeedActiveProduct()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        db.Products.Add(new Product
        {
            Id = ProductId,
            Name = ProductName,
            QuantityInStock = OpeningStock,
            CostingQuantity = OpeningStock,
            InventoryValue = OpeningStock * OpeningUnitCost,
            AverageUnitCost = OpeningUnitCost,
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = OpeningStock,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = OpeningUnitCost,
            EffectiveAt = PurchaseDate.AddDays(-1),
        });
        db.SaveChanges();
    }

    /// <summary>
    /// What a fresh context sees: no purchase, no purchase line, no movement beyond the seeded
    /// opening one, and the shelf quantity and costing position the seed left.
    /// </summary>
    private void AssertNothingWasWritten()
    {
        var state = Read();
        Assert.Equal(0, state.PurchaseCount);
        Assert.Equal(0, state.PurchaseLineCount);
        Assert.Equal(0, state.PurchaseMovementCount);
        Assert.Equal(1, state.MovementCount);
        Assert.Equal(OpeningStock, state.QuantityInStock);
        Assert.Equal(OpeningStock, state.CostingQuantity);
        Assert.Equal(OpeningStock * OpeningUnitCost, state.InventoryValue);
        Assert.Equal(OpeningUnitCost, state.AverageUnitCost);
    }

    private StoredState Read()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        var product = db.Products.AsNoTracking().Single(candidate => candidate.Id == ProductId);
        return new StoredState(
            db.Receipts.AsNoTracking().Count(),
            db.ReceiptItems.AsNoTracking().Count(),
            db.StockAdjustments.AsNoTracking().Count(movement => movement.ReceiptItemId.HasValue),
            db.StockAdjustments.AsNoTracking().Count(),
            product.QuantityInStock,
            product.CostingQuantity,
            product.InventoryValue,
            product.AverageUnitCost);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>The stored facts a refused write must have left exactly as they were.</summary>
    private sealed record StoredState(
        int PurchaseCount,
        int PurchaseLineCount,
        int PurchaseMovementCount,
        int MovementCount,
        int QuantityInStock,
        int? CostingQuantity,
        decimal? InventoryValue,
        decimal AverageUnitCost);

    /// <summary>
    /// The real purchase adapter with one hook: the instant its retired-product read has answered
    /// "none of them", <paramref name="retire"/> commits the retirement. Everything else is delegated
    /// unchanged, so the create that follows is the production write.
    /// </summary>
    private sealed class RetiringAfterTheCheckPurchaseStore(IPurchaseStore inner, Action retire) : IPurchaseStore
    {
        public async Task<IReadOnlyDictionary<long, string>> FindRetiredProductsAsync(
            IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
        {
            var retired = await inner.FindRetiredProductsAsync(productIds, cancellationToken);
            retire();
            return retired;
        }

        public Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken) =>
            inner.ListAsync(supplierId, cancellationToken);

        public Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
            inner.FindByIdAsync(id, cancellationToken);

        public Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken) =>
            inner.FindFileMetadataAsync(id, cancellationToken);

        public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) =>
            inner.SupplierExistsAsync(supplierId, cancellationToken);

        public Task<bool> AllProductsExistAsync(
            IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
            inner.AllProductsExistAsync(productIds, cancellationToken);

        public Task<(long ProductId, string? ProductName, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
            IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken) =>
            inner.FindConflictingCostTransitionBaselineAsync(productIds, purchaseDate, cancellationToken);

        public Task<PurchaseCreateOutcome> CreateAsync(
            PurchaseFields fields,
            IReadOnlyList<PurchaseItemInput> items,
            PurchaseFileMetadata? file,
            SupplierOrderCompletionDecision? supplierOrderCompletion,
            AttachmentUploadReservation? attachmentReservation,
            PurchaseIdempotencyClaim? idempotency,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(
                fields, items, file, supplierOrderCompletion, attachmentReservation, idempotency, cancellationToken);

        public Task<PurchaseIdempotentCreate?> FindByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            inner.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        public Task<PurchaseDuplicateMatch?> FindPossibleDuplicateAsync(
            int supplierId,
            string invoiceNumber,
            DateTime fromUtcInclusive,
            DateTime toUtcExclusive,
            CancellationToken cancellationToken) =>
            inner.FindPossibleDuplicateAsync(
                supplierId, invoiceNumber, fromUtcInclusive, toUtcExclusive, cancellationToken);

        public Task<PurchaseDocumentAttachment> AttachDocumentAsync(
            int id,
            PurchaseFileMetadata document,
            AttachmentUploadReservation? attachmentReservation,
            CancellationToken cancellationToken) =>
            inner.AttachDocumentAsync(id, document, attachmentReservation, cancellationToken);

        public Task<PurchaseRecord?> UpdateAsync(
            int id,
            PurchaseFields fields,
            IReadOnlyList<PurchaseItemInput>? items,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(id, fields, items, cancellationToken);

        public Task<string?> DeleteAsync(int id, CancellationToken cancellationToken) =>
            inner.DeleteAsync(id, cancellationToken);
    }

    /// <summary>
    /// The real adjustment adapter with the same one hook, on the read the use case decides
    /// retirement from.
    /// </summary>
    private sealed class RetiringAfterTheCheckStockAdjustmentStore(
        IStockAdjustmentStore inner, Action retire) : IStockAdjustmentStore
    {
        public async Task<ManualStockAdjustmentProductFacts?> FindProductFactsAsync(
            long productId, CancellationToken cancellationToken)
        {
            var facts = await inner.FindProductFactsAsync(productId, cancellationToken);
            retire();
            return facts;
        }

        public Task<bool> ProductExistsAsync(long productId, CancellationToken cancellationToken) =>
            inner.ProductExistsAsync(productId, cancellationToken);

        public Task<IReadOnlyList<StockAdjustmentRecord>> ListHistoryAsync(
            long productId, CancellationToken cancellationToken) =>
            inner.ListHistoryAsync(productId, cancellationToken);

        public Task<StockHistoryResult> QueryHistoryAsync(
            StockHistoryFilter filter, CancellationToken cancellationToken) =>
            inner.QueryHistoryAsync(filter, cancellationToken);

        public Task<RestockCostFacts?> GetRestockCostFactsAsync(
            long productId, CancellationToken cancellationToken) =>
            inner.GetRestockCostFactsAsync(productId, cancellationToken);

        public Task<StockAdjustmentRecord> ApplyAsync(
            long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken) =>
            inner.ApplyAsync(productId, input, cancellationToken);
    }

    /// <summary>
    /// Document storage that keeps what it is given in memory and records both halves, so a test can
    /// assert that the bytes a refused create wrote were taken away again.
    /// </summary>
    private sealed class RecordingDocumentStorage : IDocumentStorage
    {
        private readonly Dictionary<(DocumentCategory Category, string StoredFileName), byte[]> _documents = [];

        public List<string> SavedFileNames { get; } = [];

        public List<string?> DeletedFileNames { get; } = [];

        public async Task SaveAsync(
            DocumentCategory category,
            string storedFileName,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            _documents[(category, storedFileName)] = buffer.ToArray();
            SavedFileNames.Add(storedFileName);
        }

        public Task<DocumentContent?> OpenReadAsync(
            DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentContent?>(
                storedFileName is not null && _documents.TryGetValue((category, storedFileName), out var bytes)
                    ? new DocumentContent(new MemoryStream(bytes), bytes.Length)
                    : null);

        public Task<bool> DeleteAsync(
            DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default)
        {
            DeletedFileNames.Add(storedFileName);
            return Task.FromResult(storedFileName is not null && _documents.Remove((category, storedFileName)));
        }

        public bool Holds(string storedFileName) =>
            _documents.ContainsKey((DocumentCategory.PurchaseDocument, storedFileName));
    }
}
