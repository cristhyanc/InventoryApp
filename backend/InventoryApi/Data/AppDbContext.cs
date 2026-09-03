using Microsoft.EntityFrameworkCore;
using InventoryApi.Models;

namespace InventoryApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<NayaxSales> NayaxSales => Set<NayaxSales>();
    public DbSet<ImportedFile> ImportedFiles => Set<ImportedFile>();
    public DbSet<ImportedReimbursement> ImportedReimbursements => Set<ImportedReimbursement>();
    public DbSet<ImportedReimbursementDevice> ImportedReimbursementDevices => Set<ImportedReimbursementDevice>();
    public DbSet<ImportedDevicePayment> ImportedDevicePayments => Set<ImportedDevicePayment>();
    public DbSet<ImportedFee> ImportedFees => Set<ImportedFee>();
    public DbSet<ImportedPaymentMethod> ImportedPaymentMethods => Set<ImportedPaymentMethod>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .Property(p => p.UnitPrice)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Receipt>()
            .Property(r => r.TotalAmount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Receipt>()
            .Property(r => r.DeliveryCost)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Receipt>()
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
            .Property(s => s.Quantity)
            .HasColumnType("decimal(18,2)");

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

        modelBuilder.Entity<Receipt>()
            .HasOne(r => r.Supplier)
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Category>().HasIndex(c => c.Name);
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Name);
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku);
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
    }
}
