using Microsoft.EntityFrameworkCore;
using InventoryApi.Models;

namespace InventoryApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Business> Businesses => Set<Business>();
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
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .HasIndex(x => x.ProductId)
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

        modelBuilder.Entity<NayaxSales>()
            .HasKey(s => s.TransactionID);

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
        modelBuilder.Entity<ImportedFile>().HasIndex(f => f.FileHash).IsUnique();
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
        modelBuilder.Entity<NayaxProcessingFeeRate>().HasIndex(r => r.EffectiveFrom).IsUnique();
        modelBuilder.Entity<SiteCommissionAgreement>().Property(x => x.CommissionRate).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SiteCommissionAgreement>().HasIndex(x => new { x.SiteId, x.EffectiveFrom }).IsUnique();
        modelBuilder.Entity<CommissionPayment>().Property(x => x.Amount).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CommissionPayment>().HasIndex(x => new { x.SiteId, x.PeriodStart, x.PeriodEnd });
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
        modelBuilder.Entity<Business>().Property(b => b.Name).IsRequired();
        modelBuilder.Entity<Business>().HasIndex(b => b.Name).IsUnique();

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
