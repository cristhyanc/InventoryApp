using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the <c>Machines</c> table and gives every machine this database already refers to a
    /// local record (issue #740, parent #329 child G1).
    ///
    /// <para><strong>Additive, and releasable on its own.</strong> One new table, its indexes, and a
    /// backfill into it. No existing column, id, index or foreign key changes, and nothing is
    /// repointed at the new rows: a stored sale, refill, machine count, expense, stock event,
    /// timestamp repair and transition machine stock all still hold the provider's own machine
    /// number, and issue #741 (G2) is what moves them onto <c>Machines.Id</c>. So the code already
    /// running in Production - which reads no machine row at all, because until now a machine was
    /// never persisted - keeps working across this migration, which matters because Production
    /// applies migrations at startup.</para>
    ///
    /// <para><strong>The local key is the database's own</strong> (issue #735, decision A4): the
    /// backfill assigns no <c>Id</c>. It records the provider's number in
    /// <c>ExternalMachineId</c> as text, written with SQLite's <c>CAST(... AS TEXT)</c>, which
    /// produces exactly the invariant decimal form
    /// <c>Inventory.Domain.Providers.NayaxExternalId.From</c> writes. A local id that happens to
    /// equal a Nayax machine id is a coincidence.</para>
    ///
    /// <para><strong>Every stored machine reference is a Nayax one, and that is asserted rather than
    /// assumed.</strong> A business has one sales provider and it cannot change (issue #759,
    /// <c>BusinessSalesProviderEnforcer</c>), so a machine number stored against a business was
    /// issued by that business's provider. The migration therefore refuses - before it writes
    /// anything - if any business holds machine data while its <c>SalesProvider</c> is anything other
    /// than Nayax, including not set at all: copying such a number into a column every later lookup
    /// reads as "the provider's id for this machine" would be recording a claim nothing supports.
    /// <c>1</c> is the stored value of <c>Inventory.Domain.Providers.ExternalProvider.Nayax</c>,
    /// written as a literal because a migration is a historical statement about rows and must keep
    /// meaning the same thing if that enum is ever renumbered.</para>
    ///
    /// <para><strong>The name comes from the most recent stored data that carried one</strong> - a
    /// sale's <c>MachineName</c> at its authorization instant, or an inventory-cost transition's
    /// recorded machine name at its baseline's creation instant - and otherwise from
    /// <c>Inventory.Domain.Machines.MachineCataloguePolicy.DefaultName</c>, whose shape ("Machine"
    /// and the provider's number) is mirrored in the SQL below because a migration cannot call into
    /// the Domain. The migration test compares the two so they cannot drift.</para>
    ///
    /// <para><strong>A machine number of zero is not a machine.</strong> <c>NayaxSales.MachineID</c>
    /// and the other non-nullable columns record <c>0</c> when the row carried no machine at all, so
    /// those rows reference no machine and get none; a Nayax machine id is a positive identifier.
    /// </para>
    ///
    /// <para><strong>And it verifies the backfill afterwards.</strong> Every distinct referenced
    /// machine must have exactly one row carrying its number, and there must be no other row: any
    /// difference aborts the whole upgrade rather than leaving it half applied.</para>
    /// </summary>
    public partial class AddMachines : Migration
    {
        /// <summary>
        /// The guard the refusals below are raised through: a one-column scratch table whose inserts
        /// a trigger aborts whenever the count being inserted is not zero. SQLite can only raise an
        /// error from a trigger, so this is how a migration says "fail loudly, with the reason".
        /// </summary>
        private const string GuardTable = "MachineBackfillGuard";

        /// <summary>
        /// Every machine this database refers to, as <c>(BusinessId, MachineId)</c> - the complete
        /// set of columns that hold a provider machine number, de-duplicated by <c>UNION</c>. A
        /// number of zero is excluded: it is the absent value a non-nullable column records, not a
        /// machine.
        /// </summary>
        private const string MachineReferences =
            """
            SELECT "BusinessId", "MachineID" AS "MachineId" FROM "NayaxSales"
                WHERE "MachineID" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "NayaxMachineStockEvents"
                WHERE "MachineId" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "MachineStockCounts"
                WHERE "MachineId" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "NayaxSaleTimestampRepairs"
                WHERE "MachineId" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "InventoryCostTransitionMachineStocks"
                WHERE "MachineId" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "StockAdjustments"
                WHERE "MachineId" IS NOT NULL AND "MachineId" > 0
            UNION
            SELECT "BusinessId", "MachineId" FROM "OperatingExpenses"
                WHERE "MachineId" IS NOT NULL AND "MachineId" > 0
            """;

        /// <summary>
        /// Every machine name this database has stored, with the instant it was stored for, so the
        /// most recent one can be chosen. Only two tables ever recorded one; a blank name is not a
        /// name.
        /// </summary>
        private const string MachineNames =
            """
            SELECT "BusinessId",
                   "MachineID" AS "MachineId",
                   "MachineName" AS "Name",
                   "MachineAuthorizationTime" AS "SeenAt"
                FROM "NayaxSales"
                WHERE "MachineID" > 0 AND "MachineName" IS NOT NULL AND TRIM("MachineName") <> ''
            UNION ALL
            SELECT stock."BusinessId",
                   stock."MachineId",
                   stock."MachineName",
                   baseline."CreatedAt"
                FROM "InventoryCostTransitionMachineStocks" stock
                JOIN "InventoryCostTransitionBaselines" baseline
                  ON baseline."Id" = stock."InventoryCostTransitionBaselineId"
                WHERE stock."MachineId" > 0
                  AND stock."MachineName" IS NOT NULL AND TRIM(stock."MachineName") <> ''
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalMachineId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Machines_BusinessId",
                table: "Machines",
                column: "BusinessId");

            // Filtered, because "this machine has no provider id" is a real state - every machine of
            // a business with no provider is in it - and a plain unique index would allow exactly one
            // such row per business. Scoped by business, because two operators' fleets may
            // legitimately both contain machine 7.
            migrationBuilder.CreateIndex(
                name: "IX_Machines_BusinessId_ExternalMachineId",
                table: "Machines",
                columns: new[] { "BusinessId", "ExternalMachineId" },
                unique: true,
                filter: "\"ExternalMachineId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Machines_Name",
                table: "Machines",
                column: "Name");

            CreateGuard(migrationBuilder);

            RefuseWhenNotZero(
                migrationBuilder,
                "A business holds machine data while its sales provider is not Nayax, so the machine "
                    + "backfill was refused and no machine was created. Every stored machine number "
                    + "was issued by that business's sales provider; set the provider that issued "
                    + "them first.",
                $"""
                SELECT COUNT(*) FROM (
                    SELECT DISTINCT refs."BusinessId" FROM ({MachineReferences}) refs
                    WHERE NOT EXISTS (SELECT 1 FROM "Businesses" owner
                                      WHERE owner."Id" = refs."BusinessId"
                                        AND owner."SalesProvider" = 1))
                """);

            // The backfill. One machine per distinct referenced number per business, active, named
            // from the most recent stored name for that number or "Machine <number>" when nothing
            // ever named it. No Id is assigned: the database issues it.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "Machines" ("BusinessId", "Name", "IsActive", "ExternalMachineId")
                SELECT refs."BusinessId",
                       COALESCE(
                           (SELECT TRIM(named."Name") FROM ({MachineNames}) named
                            WHERE named."BusinessId" = refs."BusinessId"
                              AND named."MachineId" = refs."MachineId"
                            ORDER BY named."SeenAt" DESC, named."Name" ASC
                            LIMIT 1),
                           'Machine ' || CAST(refs."MachineId" AS TEXT)),
                       1,
                       CAST(refs."MachineId" AS TEXT)
                FROM ({MachineReferences}) refs;
                """);

            RefuseWhenNotZero(
                migrationBuilder,
                "The machine backfill did not produce exactly one machine for each machine number "
                    + "this database refers to, so it is refused and rolled back rather than left "
                    + "half applied.",
                $"""
                SELECT (SELECT COUNT(*) FROM ({MachineReferences}) refs
                        WHERE NOT EXISTS (SELECT 1 FROM "Machines" local
                                          WHERE local."BusinessId" = refs."BusinessId"
                                            AND local."ExternalMachineId"
                                                = CAST(refs."MachineId" AS TEXT)))
                     + ABS((SELECT COUNT(*) FROM "Machines")
                           - (SELECT COUNT(*) FROM ({MachineReferences}) counted))
                """);

            DropGuard(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The table and everything the backfill wrote into it. Nothing else to undo: no existing
            // column was changed and nothing was repointed at these rows.
            migrationBuilder.DropTable(
                name: "Machines");
        }

        /// <summary>
        /// Creates the scratch table every check below inserts its count into. It is dropped again
        /// once the checks have passed, so this migration leaves nothing behind but the machines.
        /// </summary>
        private static void CreateGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                $"""
                CREATE TABLE "{GuardTable}" ("Offending" INTEGER NOT NULL);
                """);

        private static void DropGuard(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""DROP TABLE "{GuardTable}";""");

        /// <summary>
        /// Aborts the migration with <paramref name="message"/> when <paramref name="countingSql"/>
        /// counts anything at all.
        ///
        /// <para>A trigger is the only way SQLite can raise an error with a message of its own, so
        /// one is created, the count is inserted through it, and it is dropped again. The message is
        /// a literal because <c>RAISE</c> takes no expression; it names the rule that was broken and
        /// no row data, because it reaches the startup log.</para>
        /// </summary>
        private static void RefuseWhenNotZero(
            MigrationBuilder migrationBuilder, string message, string countingSql)
        {
            var trigger = $"TR_{GuardTable}_Refuse";

            migrationBuilder.Sql(
                $"""
                CREATE TRIGGER "{trigger}" BEFORE INSERT ON "{GuardTable}"
                WHEN NEW."Offending" <> 0
                BEGIN
                    SELECT RAISE(ABORT, '{message.Replace("'", "''")}');
                END;
                """);

            migrationBuilder.Sql(
                $"""
                INSERT INTO "{GuardTable}" ("Offending") {countingSql};
                """);

            migrationBuilder.Sql($"""DROP TRIGGER "{trigger}";""");
        }
    }
}
