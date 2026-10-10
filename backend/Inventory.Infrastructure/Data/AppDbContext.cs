using System.Linq.Expressions;
using Inventory.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Data;

public class AppDbContext : DbContext
{
    private readonly IBusinessScope _businessScope;

    /// <summary>
    /// Constructs a context with no business resolved, which is the fail-closed state: every
    /// tenant-owned read returns nothing and every tenant-owned write is refused.
    ///
    /// Unrestricted, all-business access is never the default. Code that genuinely needs it -
    /// controlled setup, migrations, cross-business maintenance - must say so by passing
    /// <see cref="UnscopedBusinessScope.Instance"/> to the other constructor, so every such
    /// place is greppable and reviewable rather than implied by which overload was convenient.
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : this(options, new BusinessScope())
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, IBusinessScope businessScope)
        : base(options)
    {
        _businessScope = businessScope;
    }

    /// <summary>
    /// Read by the global query filters below. It is an instance member rather than a captured
    /// constant so EF re-evaluates it per query instead of baking the first request's business
    /// into the compiled model.
    ///
    /// A denied scope yields <c>null</c>, and the filters compare against it with <c>==</c>, so
    /// an unresolved caller matches no row at all. Failing closed here is deliberate: the
    /// alternative - treating "no business" as "no filter" - would turn a resolution bug into a
    /// silent cross-business data leak.
    /// </summary>
    public int? CurrentBusinessId => _businessScope.BusinessId;

    public bool TenantFilteringEnabled => _businessScope.State != BusinessScopeState.Unscoped;

    /// <summary>
    /// Exposed for the SaveChanges enforcement in <see cref="BusinessOwnershipEnforcer"/>, which
    /// needs the same answer this context filters reads by.
    /// </summary>
    internal IBusinessScope BusinessScope => _businessScope;

    public DbSet<Business> Businesses => Set<Business>();

    /// <summary>
    /// Operational audit of the tenancy backfill. Not business data and deliberately not
    /// tenant-filtered; see BusinessBackfillAudit.
    /// </summary>
    public DbSet<BusinessBackfillAudit> BusinessBackfillAudits => Set<BusinessBackfillAudit>();

    public DbSet<BusinessMembership> BusinessMemberships => Set<BusinessMembership>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<Purchase> Receipts => Set<Purchase>();
    public DbSet<NayaxSales> NayaxSales => Set<NayaxSales>();
    public DbSet<NayaxMachineStockEvent> NayaxMachineStockEvents => Set<NayaxMachineStockEvent>();
    public DbSet<ImportedFile> ImportedFiles => Set<ImportedFile>();
    public DbSet<ImportedReimbursement> ImportedReimbursements => Set<ImportedReimbursement>();
    public DbSet<ImportedReimbursementDevice> ImportedReimbursementDevices => Set<ImportedReimbursementDevice>();
    public DbSet<ImportedDevicePayment> ImportedDevicePayments => Set<ImportedDevicePayment>();
    public DbSet<ImportedFee> ImportedFees => Set<ImportedFee>();
    public DbSet<ImportedPaymentMethod> ImportedPaymentMethods => Set<ImportedPaymentMethod>();
    public DbSet<PurchaseItem> ReceiptItems => Set<PurchaseItem>();
    public DbSet<SupplierOrder> SupplierOrders => Set<SupplierOrder>();
    public DbSet<SupplierOrderLine> SupplierOrderLines => Set<SupplierOrderLine>();
    public DbSet<SupplierOrderReceiptAllocation> SupplierOrderReceiptAllocations => Set<SupplierOrderReceiptAllocation>();
    public DbSet<OperatingExpense> OperatingExpenses => Set<OperatingExpense>();
    public DbSet<NayaxProcessingFeeRate> NayaxProcessingFeeRates => Set<NayaxProcessingFeeRate>();

    /// <summary>
    /// Each business's own Nayax Lynx credentials, with the access token encrypted at rest and the
    /// id of the key that encrypted it (issue #518). Exactly one row per business; see
    /// <see cref="BusinessNayaxConnection"/>.
    /// </summary>
    public DbSet<BusinessNayaxConnection> BusinessNayaxConnections => Set<BusinessNayaxConnection>();
    public DbSet<SiteCommissionAgreement> SiteCommissionAgreements => Set<SiteCommissionAgreement>();
    public DbSet<CommissionPayment> CommissionPayments => Set<CommissionPayment>();
    public DbSet<InventoryCostTransitionBaseline> InventoryCostTransitionBaselines => Set<InventoryCostTransitionBaseline>();
    public DbSet<InventoryCostTransitionMachineStock> InventoryCostTransitionMachineStocks => Set<InventoryCostTransitionMachineStock>();
    public DbSet<InventoryCostTransitionPreviewDraft> InventoryCostTransitionPreviewDrafts => Set<InventoryCostTransitionPreviewDraft>();

    /// <summary>
    /// Append-only costing-only historical repairs (issue #359). Nothing in the application updates
    /// or deletes a row in this set; see <see cref="InventoryCostRepair"/>.
    /// </summary>
    public DbSet<InventoryCostRepair> InventoryCostRepairs => Set<InventoryCostRepair>();

    /// <summary>
    /// Append-only records of stored Nayax sale instants repaired from authoritative source evidence
    /// (issue #472). Nothing in the application updates or deletes a row in this set; see
    /// <see cref="NayaxSaleTimestampRepair"/>.
    /// </summary>
    public DbSet<NayaxSaleTimestampRepair> NayaxSaleTimestampRepairs => Set<NayaxSaleTimestampRepair>();

    /// <summary>The stored, single-use, expiring plans those repairs are confirmed from (issue #472).</summary>
    public DbSet<NayaxSaleTimestampRepairPreviewDraft> NayaxSaleTimestampRepairPreviewDrafts =>
        Set<NayaxSaleTimestampRepairPreviewDraft>();

    /// <summary>
    /// Both save paths funnel through <see cref="BusinessOwnershipEnforcer"/> so tenant
    /// ownership is applied to every write, whichever overload a service happens to call. This
    /// is why services do not, and must not, add their own business filters or stamping.
    ///
    /// <see cref="BusinessTimeZoneEnforcer"/> joins it for the same reason (issue #499): a
    /// business's time zone is what every business date is derived from, so an unresolvable one is
    /// refused on whichever path writes it rather than per write path.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BusinessOwnershipEnforcer.Enforce(this);
        BusinessTimeZoneEnforcer.Enforce(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        BusinessOwnershipEnforcer.Enforce(this);
        BusinessTimeZoneEnforcer.Enforce(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenancy(modelBuilder);

        modelBuilder.Entity<Product>()
            .Property(p => p.UnitPrice)
            .HasColumnType("decimal(18,2)");
        modelBuilder.Entity<Product>()
            .Property(p => p.AverageUnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<Product>()
            .Property(p => p.InventoryValue)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .Property(x => x.AverageUnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .Property(x => x.InventoryValue)
            .HasColumnType("decimal(18,6)");
        // One baseline per product, scoped by business. A product cannot span businesses, so
        // this is exactly as strong as the previous product-only constraint - but it states the
        // tenant rule explicitly and lets the index lead with the column every query filters on.
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .HasIndex(x => new { x.BusinessId, x.ProductId })
            .IsUnique();
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryCostTransitionMachineStock>()
            .HasOne(x => x.InventoryCostTransitionBaseline)
            .WithMany(x => x.MachineStocks)
            .HasForeignKey(x => x.InventoryCostTransitionBaselineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Costing repairs (issue #359). Deliberately no unique constraint: a product may need more
        // than one repair, including two at the same instant, and the replay orders them by key.
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.UnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.TotalValue)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.Reason).IsRequired();
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.CreatedByDirectoryTenantId).IsRequired();
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.CreatedByObjectId).IsRequired();
        // The replay's load path: one product's repairs, in effective-time order, within a business.
        modelBuilder.Entity<InventoryCostRepair>()
            .HasIndex(x => new { x.BusinessId, x.ProductId, x.EffectiveAt });
        modelBuilder.Entity<InventoryCostRepair>()
            .HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        // EffectiveAt and CreatedAt are persisted UTC instants, and the SQLite provider does not
        // round-trip DateTimeKind - see the StockAdjustment.CreatedAt comment below for the complete
        // explanation. Marking them UTC on every read keeps the instant the API boundary exposes
        // unambiguous without changing the stored bytes or any comparison semantics, so the replay's
        // ordering against stock movements and sales is unaffected.
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.EffectiveAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.CreatedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // Nayax sale timestamp repairs (issue #472). A transaction may legitimately be repaired more
        // than once - a later, better source can correct an earlier correction - so the audit has no
        // unique constraint, and the index is the read path: one business's repairs, newest applied
        // first, with a transaction lookup leading on the column every query filters on.
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.EvidenceReference).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.AppliedByDirectoryTenantId).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.AppliedByObjectId).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .HasIndex(x => new { x.BusinessId, x.TransactionId, x.AppliedAt });
        // The three instants are persisted UTC and are exposed by the repair API, so each is marked
        // UTC on read - see the StockAdjustment.CreatedAt comment below for the complete explanation
        // of why the SQLite provider makes that necessary. The two business dates are deliberately
        // Kind-free calendar dates and are not converted, so the audit never serialises a date as an
        // instant.
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.PreviousInstantUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.RepairedInstantUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.AppliedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // The stored plan is server-internal state, never part of an API response, so it needs no
        // Kind conversion: its two instants are only ever compared against the clock, and a DateTime
        // comparison looks at ticks and not Kind. This matches InventoryCostTransitionPreviewDraft.
        modelBuilder.Entity<NayaxSaleTimestampRepairPreviewDraft>().Property(x => x.PlanJson).IsRequired();

        // One Nayax connection per business (issue #518). The unique index is on the ownership
        // column alone, which is what makes "one per business" a schema guarantee instead of a
        // convention the adapter has to remember: a second row for a business cannot be inserted,
        // so a credential save can only ever create the first one or update the existing one.
        modelBuilder.Entity<BusinessNayaxConnection>()
            .HasIndex(connection => connection.BusinessId)
            .IsUnique();
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.OperatorId).IsRequired();
        // Required, not nullable: the row exists only once credentials have been stored, so an
        // empty ciphertext or a nameless key would be a state nothing can act on. "No credentials"
        // is the absence of the row, reported as NayaxConnectionStatus.NotConfigured.
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.AccessTokenCiphertext).IsRequired();
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.EncryptionKeyId).IsRequired();
        // Both instants are persisted UTC and are exposed through the connection read, so each is
        // marked UTC on the way out - see the StockAdjustment.CreatedAt comment below for why the
        // SQLite provider makes that necessary. The conversion changes no stored byte and no
        // comparison, so the conditional revision check is unaffected.
        modelBuilder.Entity<BusinessNayaxConnection>()
            .Property(x => x.LastTestedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => fromProvider.HasValue
                    ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                    : fromProvider);
        modelBuilder.Entity<BusinessNayaxConnection>()
            .Property(x => x.UpdatedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // Purchase/PurchaseItem are the Purchase-language CLR types; explicitly mapped to
        // their legacy "Receipt"/"ReceiptItem" tables so the rename does not change the schema.
        modelBuilder.Entity<Purchase>().ToTable("Receipts");
        modelBuilder.Entity<PurchaseItem>().ToTable("ReceiptItems");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.TotalAmount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.DeliveryCost)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.PackageCost)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<NayaxSales>()
            .ToTable("NayaxSales");

        // The key is ours; TransactionID is Nayax's. See NayaxSales.Id for why they are not
        // the same thing once more than one operator account can exist.
        modelBuilder.Entity<NayaxSales>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.Id)
            .ValueGeneratedOnAdd();

        // A Nayax transaction appears at most once per business. Scoping the constraint by
        // business is what lets two operator accounts legitimately report the same remote id.
        modelBuilder.Entity<NayaxSales>()
            .HasIndex(s => new { s.BusinessId, s.TransactionID })
            .IsUnique();

        // MachineAuthorizationTime is a persisted true UTC instant (see docs/architecture.md
        // § Nayax sale timestamps for where that instant is established - the latest-sales
        // synchronization writes it from the Nayax payload's authoritative AuthorizationDateTimeGMT
        // field, not from the identically named machine-local one, issue #380 - and
        // § Timezone and business calendar for what reporting then does with it). This conversion is
        // about Kind metadata only: it has never been, and must not be read as, evidence of what the
        // upstream field means. The Transaction Sales report exposes the instant to the
        // frontend as the transaction's instant. Microsoft's SQLite provider does not round-trip
        // DateTimeKind - see the StockAdjustment.CreatedAt comment below for the complete
        // explanation - so without this the report's transactionDate serialises with no "Z"/offset
        // and the Angular BusinessDateTimePipe parses it as browser-local time (issue #232, the
        // same defect class issue #230 fixed for Stock History). Marking the value as UTC on every
        // read restores the unambiguous instant the API boundary must expose without changing the
        // stored bytes or any comparison semantics (DateTime comparisons and SQL translation only
        // ever look at ticks, never Kind), so sale costing, COGS, fee/commission resolution,
        // reconciliation, and every date-range filter are unaffected. Values derived from this
        // instant as a business-calendar *date* are reduced back to a Kind-free date at the point
        // of derivation, so a date-only field is never serialised as a UTC instant: see
        // EfDailyReportFactsProvider and NayaxProcessingFeeService.
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.MachineAuthorizationTime)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.SettlementValue)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.UnitCostAtSale)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.CostOfGoodsSold)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.NayaxProductCostPrice)
            .HasColumnType("decimal(18,6)");

        // A Nayax machine-stock event (identified by its Nayax EventLogID) appears at most once per
        // business, the same idempotency pattern as NayaxSales/TransactionID above (issue #183).
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasIndex(e => new { e.BusinessId, e.NayaxEventLogId })
            .IsUnique();

        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasIndex(e => new { e.BusinessId, e.MachineId, e.ProcessingStatus });

        // EventDateTimeGmt is normalised to a UTC instant at import (SyncMachineStockFromNayax.AsUtc,
        // issue #217) and the Sync Restock preview exposes it to the frontend as the event's instant.
        // Microsoft's SQLite provider does not round-trip DateTimeKind - see the StockAdjustment
        // .CreatedAt comment below for the complete explanation - so without this the preview's
        // eventDateTimeGmt serialises with no "Z"/offset and the Angular BusinessDateTimePipe parses
        // it as browser-local time (issue #237, the same defect class issue #230/#232 fixed for Stock
        // History/Transaction Sales). Marking the value as UTC on every read restores the unambiguous
        // instant the API boundary must expose without changing the stored bytes or any comparison
        // semantics (DateTime comparisons and SQL translation only ever look at ticks, never Kind),
        // so the 24-hour duplicate heuristic, From-date filtering, reconciliation, idempotency,
        // inventory and costing are unaffected.
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .Property(e => e.EventDateTimeGmt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasOne(e => e.MatchedProduct)
            .WithMany()
            .HasForeignKey(e => e.MatchedProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // The manual refill a duplicate resolution was decided against (issue #196); kept for
        // auditability only, never a driver of any calculation, so it may be orphaned by SetNull.
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasOne(e => e.MatchedManualStockAdjustment)
            .WithMany()
            .HasForeignKey(e => e.MatchedManualStockAdjustmentId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Supplier)
            .WithMany(s => s.Products)
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<StockAdjustment>()
            .HasOne(sa => sa.Product)
            .WithMany(p => p.StockAdjustments)
            .HasForeignKey(sa => sa.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Purchase>()
            .HasOne(r => r.Supplier)
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PurchaseItem>()
            .HasOne(i => i.Purchase)
            .WithMany(r => r.Items)
            .HasForeignKey(i => i.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PurchaseItem>()
            .HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StockAdjustment>()
            .HasOne(a => a.ReceiptItem)
            .WithMany()
            .HasForeignKey(a => a.ReceiptItemId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PurchaseItem>().Property(i => i.Quantity).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<PurchaseItem>().Property(i => i.UnitCost).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.QuantityOrdered).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.QuantityReceived).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrder>()
            .HasOne(order => order.Supplier)
            .WithMany()
            .HasForeignKey(order => order.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<SupplierOrderLine>()
            .HasOne(line => line.SupplierOrder)
            .WithMany(order => order.Lines)
            .HasForeignKey(line => line.SupplierOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SupplierOrderLine>()
            .HasOne(line => line.Product)
            .WithMany()
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().Property(item => item.QuantityApplied).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderReceiptAllocation>()
            .HasOne(allocation => allocation.SupplierOrderLine)
            .WithMany(line => line.ReceiptAllocations)
            .HasForeignKey(allocation => allocation.SupplierOrderLineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>()
            .HasOne(allocation => allocation.ReceiptItem)
            .WithMany(item => item.SupplierOrderAllocations)
            .HasForeignKey(allocation => allocation.ReceiptItemId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperatingExpense>().Property(e => e.AmountExGst).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<OperatingExpense>().Property(e => e.GstAmount).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<OperatingExpense>().Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
        // StockAdjustment.CreatedAt is always DateTime.UtcNow (see StockAdjustment.cs), but
        // Microsoft's SQLite provider - the only provider this API runs against, see Program.cs -
        // does not round-trip DateTimeKind: a value freshly queried back from the database always
        // materialises as DateTimeKind.Unspecified. System.Text.Json then serialises it without a
        // "Z"/offset, so Stock History's JSON instant is ambiguous and the frontend's
        // BusinessDateTimePipe parses it as browser-local time instead of UTC (issue #230). Marking
        // the value as UTC on every read restores the unambiguous instant the API boundary must
        // expose, without changing the stored bytes or comparison semantics (DateTime comparisons and
        // SQL translation only ever look at ticks, never Kind).
        modelBuilder.Entity<StockAdjustment>()
            .Property(a => a.CreatedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<StockAdjustment>().Property(a => a.UnitCost).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.TotalCost).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.AverageUnitCostAfter).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.InventoryValueAfter).HasColumnType("decimal(18,6)");

        modelBuilder.Entity<Category>().HasIndex(c => c.Name);
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Name);
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku);
        modelBuilder.Entity<SupplierOrder>().HasIndex(order => new { order.SupplierId, order.Status, order.OrderDate });
        modelBuilder.Entity<SupplierOrderLine>().HasIndex(line => new { line.ProductId, line.SupplierOrderId });
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().HasIndex(allocation => allocation.ReceiptItemId);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().HasIndex(allocation => allocation.SupplierOrderLineId);
        // Import de-duplication is per business: two businesses may legitimately import the
        // same file, and one must not be told its own import is a duplicate because another
        // business imported the same bytes first.
        modelBuilder.Entity<ImportedFile>().HasIndex(f => new { f.BusinessId, f.FileHash }).IsUnique();
        modelBuilder.Entity<ImportedReimbursement>()
            .HasOne(r => r.ImportedFile)
            .WithMany(f => f.Reimbursements)
            .HasForeignKey(r => r.ImportedFileId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedReimbursementDevice>()
            .HasOne(d => d.ImportedReimbursement)
            .WithMany(r => r.Devices)
            .HasForeignKey(d => d.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedDevicePayment>()
            .HasOne(p => p.ImportedReimbursement)
            .WithMany(r => r.DevicePayments)
            .HasForeignKey(p => p.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedFee>()
            .HasOne(f => f.ImportedReimbursement)
            .WithMany(r => r.Fees)
            .HasForeignKey(f => f.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedPaymentMethod>()
            .HasOne(p => p.ImportedReimbursement)
            .WithMany(r => r.PaymentMethods)
            .HasForeignKey(p => p.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedReimbursement>().Property(r => r.Total).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalBillableTransactionAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalNotBillableTransactionAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.ServiceFee).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.ProcessingFee).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalExtraCharge).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.NetAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.TotalSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.ProcessingFees).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.ServiceFees).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalSumWithVat).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.VatPercentage).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.AverageFeeAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalCount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedPaymentMethod>().Property(p => p.TotalSalesSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<OperatingExpense>()
            .HasOne(e => e.Supplier)
            .WithMany()
            .HasForeignKey(e => e.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.ExpenseDate);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.Category);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.SupplierId);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.MachineId);
        modelBuilder.Entity<NayaxProcessingFeeRate>().Property(r => r.FeeExGst).HasColumnType("decimal(18,4)");
        // Each business configures its own effective-dated fee rates.
        modelBuilder.Entity<NayaxProcessingFeeRate>().HasIndex(r => new { r.BusinessId, r.EffectiveFrom }).IsUnique();
        modelBuilder.Entity<SiteCommissionAgreement>().Property(x => x.CommissionRate).HasColumnType("decimal(18,4)");
        // SiteId is a remote Nayax identifier, so the agreement key must be scoped by business
        // as well: the same site id from two operator accounts is two different sites.
        modelBuilder.Entity<SiteCommissionAgreement>().HasIndex(x => new { x.BusinessId, x.SiteId, x.EffectiveFrom }).IsUnique();
        modelBuilder.Entity<CommissionPayment>().Property(x => x.Amount).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CommissionPayment>().HasIndex(x => new { x.SiteId, x.PeriodStart, x.PeriodEnd });

        ConfigureBusinessOwnership(modelBuilder);
    }

    /// <summary>
    /// The one place tenant scoping is applied to reads (issue #64).
    ///
    /// It walks the model rather than naming entities, so every <see cref="IBusinessOwned"/>
    /// type gets the same treatment automatically: a required business key, an index for the
    /// scoped queries, and a global query filter. A new tenant-owned entity is therefore
    /// protected the moment it implements the interface - there is no per-entity list to forget
    /// to update, and no controller-level <c>Where</c> clause anywhere that could be omitted on
    /// one endpoint.
    ///
    /// It deliberately does <em>not</em> add a database foreign key from the business key to
    /// <see cref="Business"/>; see the comment at that point in the loop, and the required
    /// post-backfill integrity step recorded in docs/tenant-rollout.md.
    ///
    /// <see cref="Business"/> and <see cref="BusinessMembership"/> are deliberately excluded.
    /// They are the tenancy tables themselves: membership is what resolves the scope in the
    /// first place, so filtering it by the scope would be circular and would deny every caller.
    /// </summary>
    private void ConfigureBusinessOwnership(ModelBuilder modelBuilder)
    {
        // Materialised first: the loop body adds configuration, which mutates the model.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            if (!typeof(IBusinessOwned).IsAssignableFrom(clrType))
            {
                continue;
            }

            var entity = modelBuilder.Entity(clrType);

            entity.Property(nameof(IBusinessOwned.BusinessId)).IsRequired();

            // Deliberately no database foreign key to Businesses yet - this is a deferral, not a
            // decision that one is unnecessary.
            //
            // It cannot be added while unassigned rows exist: the checkpoint-2 column defaults to
            // 0, which matches no Business, so the constraint would be violated by every
            // pre-existing row and the backfill would have to fight it instead of simply
            // assigning owners. Until then the boundary rests on the query filter and
            // BusinessOwnershipEnforcer, which is sound because BusinessId is never supplied by a
            // caller - it is stamped from resolved membership - so a dangling value cannot be
            // injected through the API. Deleting a Business that still has members is already
            // blocked by BusinessMembership's restricted foreign key.
            //
            // REQUIRED POST-BACKFILL STEP: once the human-controlled bootstrap has run and no row
            // is left unassigned, add these foreign keys in a follow-up migration so referential
            // integrity backs up the application-level enforcement. See docs/tenant-rollout.md.

            // Every tenant-scoped query starts with "BusinessId = @current", so it leads.
            entity.HasIndex(nameof(IBusinessOwned.BusinessId));

            entity.HasQueryFilter(BuildBusinessFilter(clrType));
        }
    }

    /// <summary>
    /// Builds <c>e =&gt; !TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> for one
    /// entity type. Both properties are read off this context instance, so the filter tracks the
    /// current request rather than the model-building one.
    /// </summary>
    private LambdaExpression BuildBusinessFilter(Type clrType)
    {
        var entity = Expression.Parameter(clrType, "e");
        var context = Expression.Constant(this);

        var filteringDisabled = Expression.Not(
            Expression.Property(context, nameof(TenantFilteringEnabled)));

        var owned = Expression.Equal(
            Expression.Convert(
                Expression.Property(entity, nameof(IBusinessOwned.BusinessId)),
                typeof(int?)),
            Expression.Property(context, nameof(CurrentBusinessId)));

        return Expression.Lambda(Expression.OrElse(filteringDisabled, owned), entity);
    }

    /// <summary>
    /// Maps the application-owned tenancy tables introduced for issue #64: the Business that owns
    /// data, and the BusinessMembership rows that approve an authenticated Entra actor for it.
    ///
    /// This step only establishes the ownership model. Adding a TenantId to the business entities
    /// and backfilling existing records are separate, later changes.
    /// </summary>
    private static void ConfigureTenancy(ModelBuilder modelBuilder)
    {
        // Name is a display name, not an identifier: nothing resolves a business by it, so it
        // carries no uniqueness constraint and no index. Two businesses may legitimately trade
        // under the same name; ownership is decided by the key and the membership rows alone.
        modelBuilder.Entity<Business>().Property(b => b.Name).IsRequired();

        // The business's IANA time zone (issue #499). Required, because a business with no zone
        // has no derivable business dates at all; the value itself is validated against the host's
        // time-zone database by BusinessTimeZoneEnforcer, which a schema constraint cannot do.
        modelBuilder.Entity<Business>().Property(b => b.TimeZoneId).IsRequired();

        // The backfill audit is keyed only by its own id: it records what happened to a table,
        // including runs that assigned rows to the wrong business, so it must stay queryable
        // independently of the tenancy state it is evidence about.
        modelBuilder.Entity<BusinessBackfillAudit>().Property(a => a.TableName).IsRequired();
        modelBuilder.Entity<BusinessBackfillAudit>().HasIndex(a => a.RunId);

        modelBuilder.Entity<BusinessMembership>()
            .HasOne(m => m.Business)
            .WithMany(b => b.Memberships)
            .HasForeignKey(m => m.BusinessId)
            // Restrict, not Cascade: a business that still has approved actors must not be
            // deletable in a way that silently drops its authorization rows.
            .OnDelete(DeleteBehavior.Restrict);

        // Entra tid/oid are case-insensitive GUID strings. ActorIdentity already normalizes them
        // before a lookup; NOCASE additionally means a hand-seeded bootstrap row that used a
        // different casing still matches, and still collides with the unique index below rather
        // than creating a second, ambiguity-causing membership.
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.DirectoryTenantId)
            .IsRequired()
            .UseCollation("NOCASE");
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.ObjectId)
            .IsRequired()
            .UseCollation("NOCASE");

        // One membership row per (actor, business). Cross-business ambiguity is not a schema
        // constraint - it is deliberately left to BusinessMembershipResolutionPolicy, which
        // denies access rather than picking one.
        modelBuilder.Entity<BusinessMembership>()
            .HasIndex(m => new { m.DirectoryTenantId, m.ObjectId, m.BusinessId })
            .IsUnique();

        // The resolution lookup path: every membership for one authenticated actor.
        modelBuilder.Entity<BusinessMembership>()
            .HasIndex(m => new { m.DirectoryTenantId, m.ObjectId });
    }
}
