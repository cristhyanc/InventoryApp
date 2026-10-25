using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the per-machine <em>Sync Restock</em> successful-fetch coverage of issue #891: two
    /// additive nullable columns on <c>Machines</c>, and nothing else. No table is rebuilt, no index
    /// is added, no constraint changes, and every existing row keeps every value it had.
    ///
    /// <para><strong>Nothing is backfilled, deliberately.</strong> Every machine starts
    /// <c>NULL</c>/<c>NULL</c>, which the application reads as "no recorded sync" - and that is the
    /// truth rather than a claim that those machines have never been synced: restocks were being
    /// synced before this metadata existed, and the only honest statement about them is that nothing
    /// recorded it. Dating the columns from an imported event's <c>CreatedAt</c>/<c>ProcessedAt</c>,
    /// from a latest-sales or catalogue sync, or from a machine stock count would invent coverage
    /// that was never measured; the first real Sync Restock for a machine is what fills them in.</para>
    ///
    /// <para>Both columns are written by one code path only - the conditional, revision-guarded
    /// update in <c>EfMachineRestockSyncCoverageStore</c> - and neither takes part in any inventory,
    /// costing, sales or imported-event calculation. <c>Down</c> drops them, which loses only the
    /// recorded coverage: the reminder then reports every machine as having no recorded sync, which
    /// over-warns rather than claiming freshness.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddMachineRestockSyncCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastSuccessfulRestockSyncCredentialRevision",
                table: "Machines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSuccessfulRestockSyncStartedAtUtc",
                table: "Machines",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSuccessfulRestockSyncCredentialRevision",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "LastSuccessfulRestockSyncStartedAtUtc",
                table: "Machines");
        }
    }
}
