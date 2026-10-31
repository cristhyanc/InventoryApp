using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the connected-machine snapshots of issue #978 (child of #965): one new tenant-owned table
    /// of append-only billing evidence, and the account series the evidence belongs to on each
    /// business's existing Nayax connection row.
    ///
    /// <para><strong>Additive, and it changes no data.</strong> It creates one empty table and adds
    /// two columns to <c>BusinessNayaxConnections</c>. No snapshot is created - nothing is counted by
    /// a migration - no existing column is dropped, narrowed or rewritten, and no business's
    /// credentials, status, revision or last-tested instant is touched.</para>
    ///
    /// <para><strong>Every existing connection becomes account series 1.</strong> The column is added
    /// with a database default of <c>1</c>, so a business that already stored credentials is on its
    /// first account series and the first snapshot taken for it belongs to the account it is already
    /// on. <c>AccountSeriesChangedAtUtc</c> is nullable and stays <c>NULL</c> for those rows, which
    /// reads as "no change recorded" - the honest answer, because nothing knows when credentials
    /// stored before this migration were first saved.</para>
    ///
    /// <para><strong>The unique index is the mechanism, not a nicety.</strong> One snapshot per
    /// business per UTC date per account series is what makes concurrent page loads and the daily job
    /// unable to record two counts for one day: both write through one insert-if-absent operation and
    /// the loser's insert is refused here, in the database. The index is led by the business key like
    /// every other per-business uniqueness rule in this schema (AGENTS.md § Tenant ownership and data
    /// isolation), and the series is part of it because a day the business's Nayax account changed on
    /// may legitimately hold one snapshot per account.</para>
    ///
    /// <para><c>Down</c> drops the table and the two columns, which is reversible without touching a
    /// business record: the snapshots it removes are evidence this release introduced, and dropping
    /// the account series leaves the credentials, the revision and the status exactly as they are. It
    /// needs no table rebuild for the table, and SQLite can drop a plain column that no index or
    /// constraint names, which neither of these two does.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddConnectedMachineSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountSeries",
                table: "BusinessNayaxConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccountSeriesChangedAtUtc",
                table: "BusinessNayaxConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BusinessConnectedMachineSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AccountSeries = table.Column<int>(type: "INTEGER", nullable: false),
                    MachineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMachineIds = table.Column<string>(type: "TEXT", nullable: false),
                    TakenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadWasComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessConnectedMachineSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessConnectedMachineSnapshots_BusinessId",
                table: "BusinessConnectedMachineSnapshots",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessConnectedMachineSnapshots_BusinessId_SnapshotDateUtc_AccountSeries",
                table: "BusinessConnectedMachineSnapshots",
                columns: ["BusinessId", "SnapshotDateUtc", "AccountSeries"],
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BusinessConnectedMachineSnapshots");

            migrationBuilder.DropColumn(
                name: "AccountSeriesChangedAtUtc",
                table: "BusinessNayaxConnections");

            migrationBuilder.DropColumn(
                name: "AccountSeries",
                table: "BusinessNayaxConnections");
        }
    }
}
