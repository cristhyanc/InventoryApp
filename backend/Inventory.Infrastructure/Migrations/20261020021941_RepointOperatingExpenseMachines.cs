using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Repoints <c>OperatingExpenses.MachineId</c> from the provider's own machine number onto the
    /// local machine row, and makes it a foreign key (issue #780, parent #329 child G2a).
    ///
    /// <para><strong>What changes.</strong> One nullable column's meaning, for one table. Before
    /// this migration the column held the raw Nayax machine number with no foreign key; after it, it
    /// holds <c>Machines.Id</c> - this application's own key, assigned by the database (issue #735,
    /// decision A4) - and the pair <c>(BusinessId, MachineId)</c> is a foreign key against the
    /// <c>(BusinessId, Id)</c> key <c>AddMachineBusinessKey</c> declared. Nothing else is repointed:
    /// <c>StockAdjustment.MachineId</c>, <c>MachineStockCount.MachineId</c>, the provider event
    /// tables and the inventory-cost transition machine stock all still hold the provider's number,
    /// and issue #741 (the stock half of G2) is what moves them.</para>
    ///
    /// <para><strong>Every machine an expense refers to already has a local row.</strong> The
    /// <c>AddMachines</c> backfill (issue #740) created one machine per distinct machine number each
    /// business referred to, across all seven columns that held one - <c>OperatingExpenses</c>
    /// included - and recorded the number in <c>ExternalMachineId</c> as text written with SQLite's
    /// <c>CAST(... AS TEXT)</c>, which is exactly the invariant decimal form
    /// <c>Inventory.Domain.Providers.NayaxExternalId.From</c> writes. So the join below is
    /// <c>(BusinessId, ExternalMachineId = CAST(MachineId AS TEXT))</c>, scoped by business because
    /// two operators' fleets may legitimately both contain machine 7, and each business's expense
    /// must land on its own machine 7.</para>
    ///
    /// <para><strong>It refuses before it writes rather than losing an association.</strong> A
    /// non-null machine number with no matching local machine aborts the whole upgrade - nothing is
    /// updated, no constraint is added, and the previous schema stands - because the alternative is
    /// an expense silently detached from the machine it was recorded against, and a machine's
    /// operating expenses feed its direct profit. A stored <c>0</c> is one such value: it is the
    /// absent number a non-nullable machine column records elsewhere, <c>AddMachines</c>
    /// deliberately created no machine for it, and an expense carrying it has to be corrected by a
    /// person rather than guessed at here. A <c>null</c> machine - a whole-business expense - stays
    /// null and is never examined.</para>
    ///
    /// <para><strong>And it verifies the result.</strong> The update only ever touches a row whose
    /// machine it has already matched, so it cannot null an association; afterwards every non-null
    /// machine must name a machine of the same business, or the upgrade aborts rather than being
    /// left half applied. Both refusals are raised the way <c>AddMachines</c> raises its own,
    /// through a scratch table and a SQLite trigger, and the scratch table is dropped again.</para>
    ///
    /// <para><strong><c>Down</c> puts the provider's number back</strong>, because the release it
    /// returns to reads this column as one. It refuses first if any attributed machine carries no
    /// provider number, or one this application could not have written: a machine somebody created
    /// locally has none, and there is no number to go back to.</para>
    /// </summary>
    public partial class RepointOperatingExpenseMachines : Migration
    {
        /// <summary>
        /// The guard the refusals below are raised through: a one-column scratch table whose inserts
        /// a trigger aborts whenever the count being inserted is not zero. SQLite can only raise an
        /// error from a trigger, so this is how a migration says "fail loudly, with the reason".
        /// </summary>
        private const string GuardTable = "OperatingExpenseMachineRepointGuard";

        /// <summary>
        /// Whether the expense's stored provider machine number names a local machine of its own
        /// business. At most one row can match: <c>(BusinessId, ExternalMachineId)</c> carries a
        /// unique index.
        /// </summary>
        private const string MatchingMachine =
            """
            SELECT 1 FROM "Machines" local
            WHERE local."BusinessId" = expense."BusinessId"
              AND local."ExternalMachineId" = CAST(expense."MachineId" AS TEXT)
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateGuard(migrationBuilder);

            RefuseWhenNotZero(
                migrationBuilder,
                "An operating expense names a machine number that has no local machine in its "
                    + "business, so repointing the expenses onto local machines was refused and "
                    + "nothing was changed. Every machine number an expense held was given a local "
                    + "machine by the AddMachines backfill; correct the expense that names one "
                    + "nothing created - a machine number of 0 is not a machine - and start again.",
                $"""
                SELECT COUNT(*) FROM "OperatingExpenses" expense
                WHERE expense."MachineId" IS NOT NULL
                  AND NOT EXISTS ({MatchingMachine})
                """);

            // The repoint. A null machine is a whole-business expense and is left alone; every other
            // row is matched first and only then updated, so this statement cannot null an
            // association even if the guard above were ever removed.
            migrationBuilder.Sql(
                $"""
                UPDATE "OperatingExpenses" AS expense
                SET "MachineId" = (SELECT local."Id" FROM "Machines" local
                                   WHERE local."BusinessId" = expense."BusinessId"
                                     AND local."ExternalMachineId" = CAST(expense."MachineId" AS TEXT))
                WHERE expense."MachineId" IS NOT NULL
                  AND EXISTS ({MatchingMachine});
                """);

            RefuseWhenNotZero(
                migrationBuilder,
                "Repointing the operating expenses onto local machines did not leave every "
                    + "attributed expense naming a machine of its own business, so it is refused "
                    + "and rolled back rather than left half applied.",
                """
                SELECT COUNT(*) FROM "OperatingExpenses" expense
                WHERE expense."MachineId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Machines" local
                                  WHERE local."BusinessId" = expense."BusinessId"
                                    AND local."Id" = expense."MachineId")
                """);

            DropGuard(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_BusinessId_MachineId",
                table: "OperatingExpenses",
                columns: new[] { "BusinessId", "MachineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_OperatingExpenses_Machines_BusinessId_MachineId",
                table: "OperatingExpenses",
                columns: new[] { "BusinessId", "MachineId" },
                principalTable: "Machines",
                principalColumns: new[] { "BusinessId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OperatingExpenses_Machines_BusinessId_MachineId",
                table: "OperatingExpenses");

            migrationBuilder.DropIndex(
                name: "IX_OperatingExpenses_BusinessId_MachineId",
                table: "OperatingExpenses");

            CreateGuard(migrationBuilder);

            // A machine somebody created locally carries no provider number, and the release this
            // returns to reads the column as one, so there is nothing to write back and the
            // downgrade refuses. The round trip through TEXT is what checks the stored id is one
            // NayaxExternalId could have written: anything else is not a number this column held.
            RefuseWhenNotZero(
                migrationBuilder,
                "An operating expense names a local machine that carries no provider machine "
                    + "number, so putting the provider's numbers back was refused and nothing was "
                    + "changed.",
                """
                SELECT COUNT(*) FROM "OperatingExpenses" expense
                WHERE expense."MachineId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Machines" local
                                  WHERE local."BusinessId" = expense."BusinessId"
                                    AND local."Id" = expense."MachineId"
                                    AND local."ExternalMachineId" IS NOT NULL
                                    AND CAST(CAST(local."ExternalMachineId" AS INTEGER) AS TEXT)
                                        = local."ExternalMachineId")
                """);

            migrationBuilder.Sql(
                """
                UPDATE "OperatingExpenses" AS expense
                SET "MachineId" = (SELECT CAST(local."ExternalMachineId" AS INTEGER) FROM "Machines" local
                                   WHERE local."BusinessId" = expense."BusinessId"
                                     AND local."Id" = expense."MachineId")
                WHERE expense."MachineId" IS NOT NULL;
                """);

            DropGuard(migrationBuilder);
        }

        /// <summary>
        /// Creates the scratch table every check above inserts its count into. It is dropped again
        /// once the checks have passed, so this migration leaves nothing behind but the repointed
        /// column and its constraint.
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
