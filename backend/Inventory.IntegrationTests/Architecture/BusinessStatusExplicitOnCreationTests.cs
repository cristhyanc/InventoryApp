using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// The standing rule of issue #584: a new business cannot reach <see cref="BusinessStatus.Active"/>
/// because whoever created it said nothing about its status.
///
/// Issue #540 left <c>Active</c> as the entity's initialiser and as the column's database default,
/// so the two paths that create a business without stating a status - application code that forgets
/// the property, and an <c>INSERT</c> that omits the column - both produced a business whose data is
/// reachable. Each is proved shut here, at the level it has to be shut at: the entity cannot be
/// constructed without the status (a compiler rule), the value an uninitialised instance holds is
/// not a status at all, and the schema the real migration history produces has no <c>Active</c>
/// default left to supply.
///
/// This is deliberately structural rather than behavioural. The paths it protects are the ones
/// nobody has written yet, so an assertion about today's write paths would not cover them.
/// </summary>
public class BusinessStatusExplicitOnCreationTests
{
    /// <summary>
    /// The status is a <c>required</c> member, so a construction that omits it is a compile error
    /// rather than a silently active business. Reflection is how a test can state that rule: the
    /// code it forbids cannot be written down in the test either.
    /// </summary>
    [Fact]
    public void The_status_is_a_required_member_of_the_entity()
    {
        var status = typeof(Business).GetProperty(nameof(Business.Status));

        Assert.NotNull(status);
        Assert.NotNull(status!.GetCustomAttribute<RequiredMemberAttribute>());
    }

    /// <summary>
    /// And what an instance holds before anything states a status is not a status: EF
    /// materialisation, a reflective construction and a <c>default</c> struct all reach the entity
    /// without the compiler's help, and none of them must find <c>Active</c> waiting.
    /// </summary>
    [Fact]
    public void An_instance_created_without_a_status_holds_no_declared_status()
    {
        var business = Activator.CreateInstance<Business>();

        Assert.NotEqual(BusinessStatus.Active, business.Status);
        Assert.False(BusinessStatuses.IsSupported(business.Status));
    }

    /// <summary>
    /// The schema the migrations produce agrees. An <c>INSERT</c> that omits <c>Status</c> is
    /// outside the application entirely - a maintenance script, a future repair command - so the
    /// column's own default is the last thing that could hand out <c>Active</c>, and it no longer
    /// does: the default is a value no status is declared at, which
    /// <c>BusinessMembershipResolutionPolicy</c> denies.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_holds_no_active_default_for_the_status_column()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var db = TestAppDbContext.Unrestricted(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();

        var declared = await Adapters.Persistence.MigrationSchemaProbe
            .ColumnDefaultAsync(connection, "Businesses", "Status");

        // A column with no default at all would satisfy this rule too; SQLite cannot drop one
        // without rebuilding the table, so the migration replaces it with a value that is not a
        // status instead. Either way, nothing about the schema says Active.
        if (declared is not null)
        {
            var stored = int.Parse(declared, CultureInfo.InvariantCulture);

            Assert.NotEqual((int)BusinessStatus.Active, stored);
            Assert.False(BusinessStatuses.IsSupported((BusinessStatus)stored));
        }
    }
}
