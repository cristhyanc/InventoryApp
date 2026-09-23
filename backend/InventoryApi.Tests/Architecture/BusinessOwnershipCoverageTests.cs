using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Turns the issue #64 DbSet audit into a standing rule instead of a one-off review.
///
/// Every persisted entity is either tenant-owned or deliberately global, and this test holds the
/// list of deliberate exceptions. Adding a new entity without ownership fails here, which is the
/// point: the failure mode this boundary has to prevent is a table quietly arriving later with
/// no owner and no filter, readable by every business.
/// </summary>
public class BusinessOwnershipCoverageTests
{
    /// <summary>
    /// The only entities that are not tenant-owned, each for a structural reason.
    ///
    /// <see cref="Business"/> is the ownership boundary itself, and <see cref="BusinessMembership"/>
    /// is what resolves a caller to one. Filtering membership by the current business would be
    /// circular - resolution reads it before any business is known - and would deny every caller.
    ///
    /// Nothing else is exempt. In particular there is no "global reference data" in this schema:
    /// the enum-like constants (payment and costing status, expense category, commission basis,
    /// stock adjustment reason) live in code as C# enums, not tables, and every configuration
    /// table that does exist - Nayax processing fee rates included - is business configuration
    /// and is owned.
    /// </summary>
    private static readonly HashSet<Type> DeliberatelyGlobalEntities =
    [
        typeof(Business),
        typeof(BusinessMembership),
    ];

    private static IModel BuildModel()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var context = new AppDbContext(options);
        return context.Model;
    }

    [Fact]
    public void Every_persisted_entity_is_either_business_owned_or_a_declared_global()
    {
        var unowned = BuildModel()
            .GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Where(clrType => !typeof(IBusinessOwned).IsAssignableFrom(clrType))
            .Where(clrType => !DeliberatelyGlobalEntities.Contains(clrType))
            .Select(clrType => clrType.Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unowned.Length == 0,
            "These persisted entities have no business ownership and are not declared global: "
                + $"{string.Join(", ", unowned)}. Implement IBusinessOwned on a tenant-owned "
                + "entity, or add it to DeliberatelyGlobalEntities with the reason it is not "
                + "owned by a business. See docs/architecture.md and issue #64.");
    }

    /// <summary>
    /// Ownership without a query filter would be decoration. Each owned entity must actually be
    /// filtered, including the child entities whose ownership a parent already implies: EF does
    /// not apply a parent's filter to a direct query against the child's own DbSet.
    /// </summary>
    [Fact]
    public void Every_business_owned_entity_has_a_query_filter_and_an_index()
    {
        var model = BuildModel();

        var owned = model
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .ToArray();

        Assert.NotEmpty(owned);

        var unfiltered = owned
            .Where(entityType => entityType.GetDeclaredQueryFilters() is not { Count: > 0 })
            .Select(entityType => entityType.ClrType.Name)
            .ToArray();

        Assert.True(
            unfiltered.Length == 0,
            $"Business-owned entities without a global query filter: {string.Join(", ", unfiltered)}.");

        var unindexed = owned
            .Where(entityType => !entityType.GetIndexes().Any(index =>
                index.Properties.Any(property => property.Name == nameof(IBusinessOwned.BusinessId))))
            .Select(entityType => entityType.ClrType.Name)
            .ToArray();

        Assert.True(
            unindexed.Length == 0,
            $"Business-owned entities without an index on BusinessId: {string.Join(", ", unindexed)}.");
    }

    /// <summary>
    /// Documents the ownership decision for the areas issue #64 called out by name, so a change
    /// to any of them is a deliberate edit here rather than a silent drift.
    /// </summary>
    [Theory]
    [InlineData(typeof(Product))]
    [InlineData(typeof(Category))]
    [InlineData(typeof(Supplier))]
    [InlineData(typeof(Purchase))]
    [InlineData(typeof(PurchaseItem))]
    [InlineData(typeof(StockAdjustment))]
    [InlineData(typeof(SupplierOrder))]
    [InlineData(typeof(SupplierOrderLine))]
    [InlineData(typeof(SupplierOrderReceiptAllocation))]
    [InlineData(typeof(NayaxSales))]
    [InlineData(typeof(OperatingExpense))]
    [InlineData(typeof(SiteCommissionAgreement))]
    [InlineData(typeof(CommissionPayment))]
    [InlineData(typeof(NayaxProcessingFeeRate))]
    [InlineData(typeof(InventoryCostTransitionBaseline))]
    [InlineData(typeof(InventoryCostTransitionMachineStock))]
    [InlineData(typeof(InventoryCostTransitionPreviewDraft))]
    [InlineData(typeof(ImportedFile))]
    [InlineData(typeof(ImportedReimbursement))]
    [InlineData(typeof(ImportedReimbursementDevice))]
    [InlineData(typeof(ImportedDevicePayment))]
    [InlineData(typeof(ImportedFee))]
    [InlineData(typeof(ImportedPaymentMethod))]
    public void The_named_entity_is_business_owned(Type entityType)
    {
        Assert.True(
            typeof(IBusinessOwned).IsAssignableFrom(entityType),
            $"{entityType.Name} is tenant-owned data and must implement IBusinessOwned.");
    }
}
