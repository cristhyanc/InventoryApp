using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// What the database itself refuses about one issued piece of collector work (issue #924, parent
/// #817).
///
/// <para>These rules are the database's as well as <c>PriceCollectorWorkItemPolicy</c>'s, for the
/// reason every constraint in this catalogue is: a stored row is not something an application-level
/// check can be trusted to have prevented, and two concurrent issues can both pass a <c>WHERE</c>
/// clause. Relational SQLite over the real migration history, because a check constraint, a unique
/// index and a restricted foreign key are exactly what an InMemory provider does not have.</para>
/// </summary>
public class PriceCollectorWorkItemConstraintTests
{
    private static readonly DateTime IssuedAt = new(2026, 10, 28, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A work item names exactly one product. Both at once is a search claiming to be about two
    /// products; neither is work no result could ever be attributed to.
    /// </summary>
    [Fact]
    public async Task A_work_item_naming_two_products_or_none_is_refused()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var run = await fixture.SeedRunAsync(IssuedAt);

        var both = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.InsertWorkItemAsync(
            run, fixture.SupplierPresetId, masterProductId: master, waitingKey: "16:COLA|5:375ML"));
        var neither = await Assert.ThrowsAsync<DbUpdateException>(() => fixture.InsertWorkItemAsync(
            run, fixture.SupplierPresetId, masterProductId: null, waitingKey: null));

        // The database's own check, named, so a rule removed from the schema fails here rather than
        // being replaced by whatever else happened to refuse the row.
        Assert.All(
            new[] { both, neither },
            refusal => Assert.Contains(
                "CK_PriceCollectorWorkItems_OneProduct",
                Assert.IsType<SqliteException>(refusal.InnerException).Message,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// One work item per product and supplier in a run, which is the database's rule rather than a
    /// read-then-insert two concurrent issues could both pass. The same pair in a <em>different</em>
    /// run is a different piece of work and is accepted.
    /// </summary>
    [Fact]
    public async Task One_work_item_per_product_and_supplier_in_a_run()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var firstRun = await fixture.SeedRunAsync(IssuedAt);

        await fixture.InsertWorkItemAsync(firstRun, fixture.SupplierPresetId, master, waitingKey: null);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.InsertWorkItemAsync(
            firstRun, fixture.SupplierPresetId, master, waitingKey: null));

        var secondRun = await fixture.SeedRunAsync(IssuedAt.AddHours(24));
        await fixture.InsertWorkItemAsync(secondRun, fixture.SupplierPresetId, master, waitingKey: null);

        Assert.Equal(2, (await fixture.AllWorkItemsAsync()).Count);
    }

    /// <summary>
    /// An outcome and the instant it was recorded at travel together: an issued work item has
    /// neither, a reported one has both, and an outcome with no instant is an answer nobody can place
    /// in time.
    /// </summary>
    [Fact]
    public async Task An_outcome_and_its_instant_travel_together()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var run = await fixture.SeedRunAsync(IssuedAt);

        var outcomeWithNoInstant = await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.InsertWorkItemAsync(
                run,
                fixture.SupplierPresetId,
                master,
                waitingKey: null,
                outcome: PriceCollectorWorkItemOutcome.Complete,
                outcomeAtUtc: null));

        var instantWithNoOutcome = await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.InsertWorkItemAsync(
                run,
                fixture.SupplierPresetId,
                master,
                waitingKey: null,
                outcome: null,
                outcomeAtUtc: IssuedAt.AddMinutes(5)));

        Assert.All(
            new[] { outcomeWithNoInstant, instantWithNoOutcome },
            refusal => Assert.Contains(
                "CK_PriceCollectorWorkItems_OutcomeAndInstantTogether",
                Assert.IsType<SqliteException>(refusal.InnerException).Message,
                StringComparison.Ordinal));

        // And the two together are accepted, which is what #937 will write.
        await fixture.InsertWorkItemAsync(
            run,
            fixture.SupplierPresetId,
            master,
            waitingKey: null,
            outcome: PriceCollectorWorkItemOutcome.Complete,
            outcomeAtUtc: IssuedAt.AddMinutes(5));

        var stored = Assert.Single(await fixture.AllWorkItemsAsync());
        Assert.Equal(PriceCollectorWorkItemOutcome.Complete, stored.Outcome);
        Assert.Equal(IssuedAt.AddMinutes(5), stored.OutcomeAtUtc);
    }

    /// <summary>
    /// A run with work issued against it cannot be deleted, and neither can a supplier master record
    /// or a master product that work names. Work nobody can place is worse than work nobody did.
    /// </summary>
    [Fact]
    public async Task Nothing_a_work_item_names_can_be_deleted_from_under_it()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var master = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        var run = await fixture.SeedRunAsync(IssuedAt);
        await fixture.InsertWorkItemAsync(run, fixture.SupplierPresetId, master, waitingKey: null);

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.DeleteRunAsync(run));
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.DeleteMasterProductAsync(master));
    }

    /// <summary>
    /// A work item carries no business column at all - the tenancy decision read from a stored row
    /// rather than from the model - and the collector work items table has no business key for any
    /// statement to name.
    /// </summary>
    [Fact]
    public async Task A_work_item_carries_no_business_column()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        Assert.DoesNotContain(
            "BusinessId",
            await fixture.ColumnNamesAsync("PriceCollectorWorkItems"));
        Assert.Null(typeof(PriceCollectorWorkItem).GetProperty("BusinessId"));
    }
}
