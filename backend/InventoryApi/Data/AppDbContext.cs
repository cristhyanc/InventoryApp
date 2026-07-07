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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .Property(p => p.UnitPrice)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Receipt>()
            .Property(r => r.TotalAmount)
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

        modelBuilder.Entity<Category>().HasIndex(c => c.Name).IsUnique();
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Name);
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku);
    }
}
