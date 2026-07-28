using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

public class CategoryServiceTests
{
    private static AppDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Create_Update_Delete_Category()
    {
        using var db = CreateDbContext("cat_test");
        ICategoryService svc = new CategoryService(db);

        var created = await svc.Create("c1", "desc");
        Assert.NotNull(created);
        Assert.Equal("c1", created.Name);

        var got = await svc.Get(created.Id);
        Assert.NotNull(got);

        var ok = await svc.Update(created.Id, "c1-up", "d2");
        Assert.True(ok);

        var deleted = await svc.Delete(created.Id);
        Assert.True(deleted);
    }
}
