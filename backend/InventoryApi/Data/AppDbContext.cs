using System.Linq.Expressions;
using Inventory.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using InventoryApi.Models;

namespace InventoryApi.Data;

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
    public DbSet<SiteCommissionAgreement> SiteCommissionAgreements => Set<SiteCommissionAgreement>();
    public DbSet<CommissionPayment> CommissionPayments => Set<CommissionPayment>();
    public DbSet<InventoryCostTransitionBaseline> InventoryCostTransitionBaselines => Set<InventoryCostTransitionBaseline>();
    public DbSet<InventoryCostTransitionMachineStock> InventoryCostTransitionMachineStocks => Set<InventoryCostTransitionMachineStock>();
    public DbSet<InventoryCostTransitionPreviewDraft> InventoryCostTransitionPreviewDrafts => Set<InventoryCostTransitionPreviewDraft>();

    /// <summary>
    /// Both save paths funnel through <see cref="BusinessOwnershipEnforcer"/> so tenant
    /// ownership is applied to every write, whichever overload a service happens to call. This
    /// is why services do not, and must not, add their own business filters or stamping.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BusinessOwnershipEnforcer.Enforce(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        BusinessOwnershipEnforcer.Enforce(this);
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
    /// type gets the same treatment automatically: a required business key, a foreign key to
    /// <see cref="Business"/>, an index for the scoped queries, and a global query filter. A new
    /// tenant-owned entity is therefore protected the moment it implements the interface - there
    /// is no per-entity list to forget to update, and no controller-level <c>Where</c> clause
    /// anywhere that could be omitted on one endpoint.
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

            // Deliberately no database foreign key to Businesses.
            //
            // BusinessId is never supplied by a caller - it is stamped from resolved membership
            // by BusinessOwnershipEnforcer - so a dangling value cannot be injected through the
            // API, and the ownership boundary is enforced by the query filter and that enforcer
            // rather than by referential integrity. Adding the constraint would also mean the
            // additive column this migration introduces (default 0, owned by nobody) left every
            // pre-existing row in violation of it, and would make the later backfill fight the
            // constraint instead of simply assigning owners. Deleting a Business that still has
            // members is already blocked by BusinessMembership's restricted foreign key.

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
