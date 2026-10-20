using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Repoints <c>StockAdjustments.MachineId</c> and <c>MachineStockCounts.MachineId</c> from the
    /// provider's own machine number onto the local machine row (issue #741, parent #329 child G2,
    /// the stock half; the expense half was <c>RepointOperatingExpenseMachines</c>, issue #780). The
    /// foreign keys that then hold each to its machine are the next migration,
    /// <c>AddStockMachineForeignKeys</c>, which says why they are separate.
    ///
    /// <para><strong>What changes.</strong> Two columns' meaning. Before this migration each held the
    /// raw Nayax machine number with no foreign key; after it, each holds <c>Machines.Id</c> - this
    /// application's own key, assigned by the database - and the pair <c>(BusinessId,
    /// MachineId)</c> names a row of the <c>(BusinessId, Id)</c> key <c>AddMachineBusinessKey</c>
    /// declared. The provider's own data is deliberately not touched:
    /// <c>NayaxSales.MachineID</c>, <c>NayaxMachineStockEvents.MachineId</c>,
    /// <c>NayaxSaleTimestampRepairs.MachineId</c> and the inventory-cost transition's recorded
    /// <c>InventoryCostTransitionMachineStocks.MachineId</c> keep the provider's number, and a reader
    /// that needs both resolves one to the other through <c>LocalMachine.ExternalMachineId</c>.</para>
    ///
    /// <para><strong>Every machine a movement or a count refers to already has a local row.</strong>
    /// The <c>AddMachines</c> backfill (issue #740) created one machine per distinct machine number
    /// each business referred to, across every column that held one - both of these included - and
    /// recorded the number in <c>ExternalMachineId</c> as text written with SQLite's <c>CAST(... AS
    /// TEXT)</c>, the invariant decimal form <c>Inventory.Domain.Providers.NayaxExternalId.From</c>
    /// writes. So the join is <c>(BusinessId, ExternalMachineId = CAST(MachineId AS TEXT))</c>,
    /// scoped by business because two operators' fleets may both contain machine 7.</para>
    ///
    /// <para><strong>It refuses before it writes rather than losing an association.</strong> A
    /// machine number with no matching local machine in its business aborts the whole upgrade -
    /// nothing is updated, no constraint is added, and the previous schema stands - because the
    /// alternative is a refill or a count silently detached from the machine it happened in, and the
    /// machine-stock ledger is built from exactly those rows. A stored <c>0</c> is one such value:
    /// <c>AddMachines</c> created no machine for it, and a row carrying it has to be corrected by a
    /// person rather than guessed at here. A <c>null</c> stock movement machine - a movement that
    /// names no machine, which is most of them - stays null and is never examined.</para>
    ///
    /// <para><strong>And it verifies the result.</strong> Each update only touches a row whose
    /// machine it has already matched, so it cannot null an association; afterwards every machine
    /// reference must name a machine of the same business, or the upgrade aborts rather than being
    /// left half applied. The refusals are raised the way <c>AddMachines</c> and
    /// <c>RepointOperatingExpenseMachines</c> raise theirs, through a scratch table and a SQLite
    /// trigger, and the scratch table is dropped again.</para>
    ///
    /// <para><strong><c>Down</c> puts the provider's number back</strong>, because the release it
    /// returns to reads both columns as one. It refuses first if any referenced machine carries no
    /// provider number, or one this application could not have written: a machine somebody created
    /// locally has none, and there is no number to go back to.</para>
    /// </summary>
    public partial class RepointStockMachineReferences : Migration
    {
        /// <summary>The scratch table and trigger the refusals below are raised through.</summary>
        private static readonly SqliteRefusalGuard Guard = new("StockMachineRepointGuard");

        /// <summary>
        /// Whether the referencing row's stored provider machine number names a local machine of its
        /// own business. At most one machine can match: <c>(BusinessId, ExternalMachineId)</c>
        /// carries a unique index.
        /// </summary>
        private const string MatchingMachineByProviderNumber =
            """
            SELECT 1 FROM "Machines" local
            WHERE local."BusinessId" = target."BusinessId"
              AND local."ExternalMachineId" = CAST(target."MachineId" AS TEXT)
            """;

        /// <summary>The two repointed tables. Both name their machine in a column called <c>MachineId</c>.</summary>
        private static readonly string[] Tables = ["StockAdjustments", "MachineStockCounts"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Guard.Create(migrationBuilder);

            // Both tables are checked before either is written, so a refusal on the second leaves the
            // first untouched as well.
            foreach (var table in Tables)
            {
                Guard.RefuseWhenNotZero(
                    migrationBuilder,
                    $"A row of {table} names a machine number that has no local machine in its "
                        + "business, so repointing the stock machine references onto local machines was "
                        + "refused and nothing was changed. Every machine number these rows held was "
                        + "given a local machine by the AddMachines backfill; correct the row that names "
                        + "one nothing created - a machine number of 0 is not a machine - and start again.",
                    $"""
                    SELECT COUNT(*) FROM "{table}" target
                    WHERE target."MachineId" IS NOT NULL
                      AND NOT EXISTS ({MatchingMachineByProviderNumber})
                    """);
            }

            // The repoint. A null machine names no machine and is left alone; every other row is
            // matched first and only then updated, so this statement cannot null an association even
            // if the guard above were ever removed.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql(
                    $"""
                    UPDATE "{table}" AS target
                    SET "MachineId" = (SELECT local."Id" FROM "Machines" local
                                       WHERE local."BusinessId" = target."BusinessId"
                                         AND local."ExternalMachineId" = CAST(target."MachineId" AS TEXT))
                    WHERE target."MachineId" IS NOT NULL
                      AND EXISTS ({MatchingMachineByProviderNumber});
                    """);
            }

            foreach (var table in Tables)
            {
                Guard.RefuseWhenNotZero(
                    migrationBuilder,
                    $"Repointing {table} onto local machines did not leave every machine reference "
                        + "naming a machine of its own business, so it is refused and rolled back rather "
                        + "than left half applied.",
                    $"""
                    SELECT COUNT(*) FROM "{table}" target
                    WHERE target."MachineId" IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM "Machines" local
                                      WHERE local."BusinessId" = target."BusinessId"
                                        AND local."Id" = target."MachineId")
                    """);
            }

            Guard.Drop(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Guard.Create(migrationBuilder);

            // A machine somebody created locally carries no provider number, and the release this
            // returns to reads both columns as one, so there is nothing to write back and the
            // downgrade refuses. The round trip through TEXT is what checks the stored id is one
            // NayaxExternalId could have written: anything else is not a number these columns held.
            foreach (var table in Tables)
            {
                Guard.RefuseWhenNotZero(
                    migrationBuilder,
                    $"A row of {table} names a local machine that carries no provider machine number, "
                        + "so putting the provider's numbers back was refused and nothing was changed.",
                    $"""
                    SELECT COUNT(*) FROM "{table}" target
                    WHERE target."MachineId" IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM "Machines" local
                                      WHERE local."BusinessId" = target."BusinessId"
                                        AND local."Id" = target."MachineId"
                                        AND local."ExternalMachineId" IS NOT NULL
                                        AND CAST(CAST(local."ExternalMachineId" AS INTEGER) AS TEXT)
                                            = local."ExternalMachineId")
                    """);
            }

            foreach (var table in Tables)
            {
                migrationBuilder.Sql(
                    $"""
                    UPDATE "{table}" AS target
                    SET "MachineId" = (SELECT CAST(local."ExternalMachineId" AS INTEGER) FROM "Machines" local
                                       WHERE local."BusinessId" = target."BusinessId"
                                         AND local."Id" = target."MachineId")
                    WHERE target."MachineId" IS NOT NULL;
                    """);
            }

            Guard.Drop(migrationBuilder);
        }
    }
}
