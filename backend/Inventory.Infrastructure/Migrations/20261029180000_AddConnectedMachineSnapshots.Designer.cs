using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (issue #978).
    ///
    /// <para><strong>It carries no <c>BuildTargetModel</c>, and that is deliberate rather than
    /// missing</strong> - the same decision, for the same reason, as
    /// <c>20261029120000_AllowPurchaseWithoutDocument</c> and
    /// <c>20261028120000_AddPriceCollectorResults</c>. This migration was written by hand, so there is
    /// no generated point-in-time model to record here, and a hand-copied one would be a second model
    /// that could silently disagree with the real one. The authoritative target model is
    /// <c>AppDbContextModelSnapshot</c>, which was updated with this migration and is what
    /// <c>MigrationRelocationTests</c>' pending-model-changes assertion compares the live model
    /// against. Nothing at runtime reads a migration's target model: a deployment applies migrations
    /// by <c>MigrationId</c> and runs their operations, and this migration's operations - one created
    /// table, two added columns, two created indexes, and their drops - need no model at all.</para>
    ///
    /// <para>The id is on its own file rather than on the migration class so the pairing a reader
    /// expects - one migration, one designer - still holds, and so the migration file itself stays
    /// the schema change and nothing else.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261029180000_AddConnectedMachineSnapshots")]
    public partial class AddConnectedMachineSnapshots
    {
    }
}
