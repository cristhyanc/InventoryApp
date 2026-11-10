using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.NayaxSqs;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The Dashboard's "costs need attention" read over a real SQLite database: only pending rebuilds
/// whose last attempt failed, with the product's current name, and only the caller's own business.
/// </summary>
public class EfProductCostAttentionReaderTests
{
    private static readonly DateTime Created = new(2026, 11, 10, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Attempted = new(2026, 11, 10, 3, 3, 54, DateTimeKind.Utc);

    [Fact]
    public async Task Lists_only_this_business_products_whose_last_rebuild_failed()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await using (var setup = fixture.Unrestricted())
        {
            setup.PendingInventoryCostRebuilds.AddRange(
                Pending(NayaxSqsSaleFixture.BusinessA, NayaxSqsSaleFixture.ProductA, failureCode: "data_quality", attempts: 3),
                // Still waiting for its first replay: nothing has failed, so nothing needs attention.
                Pending(NayaxSqsSaleFixture.BusinessA, 5099, failureCode: null, attempts: 0),
                // Another business's failure must never reach this business's Dashboard.
                Pending(NayaxSqsSaleFixture.BusinessB, NayaxSqsSaleFixture.ProductB, failureCode: "data_quality", attempts: 1));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var products = await new EfProductCostAttentionReader(db).ListAsync(CancellationToken.None);

        var product = Assert.Single(products);
        Assert.Equal(NayaxSqsSaleFixture.ProductA, product.ProductId);
        Assert.Equal("Fictional Spring Water 600ml", product.ProductName);
        Assert.Equal("data_quality", product.FailureCode);
        Assert.Equal(3, product.AttemptCount);
        Assert.Equal(Attempted, product.LastAttemptAtUtc);
        Assert.Equal(DateTimeKind.Utc, product.LastAttemptAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Is_empty_when_no_rebuild_has_failed()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();

        await using var db = fixture.Context(NayaxSqsSaleFixture.BusinessA);
        var products = await new EfProductCostAttentionReader(db).ListAsync(CancellationToken.None);

        Assert.Empty(products);
    }

    private static PendingInventoryCostRebuild Pending(int businessId, long productId, string? failureCode, int attempts) =>
        new()
        {
            BusinessId = businessId,
            ProductId = productId,
            FromInstantUtc = Created,
            Revision = 1,
            CreatedAtUtc = Created,
            AttemptCount = attempts,
            LastAttemptAtUtc = failureCode is null ? null : Attempted,
            LastFailureCode = failureCode,
        };
}
