using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the provider data coverage record (issue #730, parent #329 decision P9): what each fully
    /// successful provider sync or committed import actually accounted for.
    ///
    /// <para><strong>Additive, and releasable on its own.</strong> No existing table, column, index
    /// or row changes here, and nothing already running in Production reads this table, which is
    /// what makes it safe for the automatic startup migration to apply it before the deployment that
    /// does (AGENTS.md § Database and migrations).</para>
    ///
    /// <para><strong>No backfill, deliberately.</strong> A coverage row is evidence that one run
    /// retrieved one range, and no such evidence exists for anything that happened before this table
    /// did: the sales synchronization reads a rolling window it never recorded, and settlement
    /// imports were recorded per file with no period. Writing rows for that history would be
    /// inventing the very claim the table exists to make. The reports instead report data that
    /// predates coverage recording as <c>unverified</c> - which is exactly what it is - so an empty
    /// table on the day this ships is the correct starting state and not a gap to be filled.</para>
    ///
    /// <para><strong>No unique constraint, also deliberately.</strong> Two successful runs
    /// legitimately claim overlapping ranges, because each reads a window that reaches back past the
    /// previous run; a unique index would reject the second and turn a successful run into no
    /// evidence at all. The availability policy merges ranges rather than counting them, so
    /// duplication costs nothing.</para>
    /// </summary>
    public partial class AddProviderDataCoverageTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderDataCoverages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderDataCoverages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDataCoverages_BusinessId",
                table: "ProviderDataCoverages",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDataCoverages_BusinessId_Provider_Source_FromUtc",
                table: "ProviderDataCoverages",
                columns: new[] { "BusinessId", "Provider", "Source", "FromUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderDataCoverages");
        }
    }
}
