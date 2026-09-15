using InventoryApi.Models;

namespace InventoryApi.Data;

public static class DbInitializer
{
    public static void Seed(AppDbContext context)
    {
        context.Database.EnsureCreated();

        if (context.Categories.Any()) return; // already seeded

        var snacksCat = new Category { Name = "Snacks", Description = "Chips, candy, and salty/sweet snacks" };
        var drinksCat = new Category { Name = "Drinks", Description = "Sodas, water, juices" };
        context.Categories.AddRange(snacksCat, drinksCat);

        var supplier = new Supplier
        {
            Name = "General Distributors Inc.",
            ContactName = "Jane Doe",
            Phone = "555-0100",
            Email = "orders@gendist.example.com"
        };
        context.Suppliers.Add(supplier);
        context.SaveChanges();

        context.Products.AddRange(
            new Product
            {
                Name = "Potato Chips",
                Sku = "SNK-001",
                UnitPrice = 2.50m,
                QuantityInStock = 40,
                LowStockThreshold = 10,
                RestockTo = 10,
                Unit = "bag",
                CategoryId = snacksCat.Id,
                SupplierId = supplier.Id
            },
            new Product
            {
                Name = "Chocolate Bar",
                Sku = "SNK-002",
                UnitPrice = 1.75m,
                QuantityInStock = 8,
                LowStockThreshold = 10,
                RestockTo = 10,
                Unit = "bar",
                CategoryId = snacksCat.Id,
                SupplierId = supplier.Id
            },
            new Product
            {
                Name = "Cola Can",
                Sku = "DRK-001",
                UnitPrice = 1.25m,
                QuantityInStock = 60,
                LowStockThreshold = 20,
                RestockTo = 20,
                Unit = "can",
                CategoryId = drinksCat.Id,
                SupplierId = supplier.Id
            },
            new Product
            {
                Name = "Bottled Water",
                Sku = "DRK-002",
                UnitPrice = 1.00m,
                QuantityInStock = 5,
                LowStockThreshold = 15,
                RestockTo = 15,
                Unit = "bottle",
                CategoryId = drinksCat.Id,
                SupplierId = supplier.Id
            }
        );

        context.SaveChanges();
    }
}
