using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (issue #937).
    ///
    /// <para><strong>It carries no <c>BuildTargetModel</c>, and that is deliberate rather than
    /// missing.</strong> This migration was written by hand - the EF Core design-time tool could not
    /// be run where it was authored - so there is no generated point-in-time model to record here,
    /// and a hand-copied one would be a second model that could silently disagree with the real one.
    /// The authoritative target model is <c>AppDbContextModelSnapshot</c>, which was updated with
    /// this migration and is what <c>MigrationRelocationTests</c>' pending-model-changes assertion
    /// compares the live model against. Four earlier migrations in this history are written the same
    /// way - <c>20260906213000_AddNayaxProcessingFeeRates</c>,
    /// <c>20260906233500_RemoveNayaxSalesQuantity</c>,
    /// <c>20260907114500_ConvertCommissionRatesToFractions</c> and
    /// <c>20260907123300_RemoveProductMapped</c> - and nothing at runtime reads a migration's target
    /// model: a deployment applies migrations by <c>MigrationId</c> and runs their operations.</para>
    ///
    /// <para>The id is on its own file rather than on the migration class so the pairing a reader
    /// expects - one migration, one designer - still holds, and so the migration file itself stays
    /// the schema change and nothing else.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261028120000_AddPriceCollectorResults")]
    public partial class AddPriceCollectorResults
    {
    }
}
