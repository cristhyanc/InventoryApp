using InventoryApi.Data;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class SupplierServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    [Fact]
    public async Task Create_Update_Delete_Supplier()
    {
        using var db = CreateDbContext("sup_test");
        ISupplierService svc = new SupplierService(db);

        var created = await svc.Create("s1", "cn", "p", "e", "a");
        Assert.NotNull(created);
        Assert.Equal("s1", created.Name);

        var ok = await svc.Update(created.Id, "s1-up", "cn2", "p2", "e2", "a2");
        Assert.True(ok);

        var deleted = await svc.Delete(created.Id);
        Assert.True(deleted);
    }
}
