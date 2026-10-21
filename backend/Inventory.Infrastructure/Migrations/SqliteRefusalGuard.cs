using Microsoft.EntityFrameworkCore.Migrations;

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// How a data migration refuses on SQLite (issue #741): a one-column scratch table whose inserts a
    /// trigger aborts whenever the count inserted is not zero. A trigger is the only way SQLite can
    /// raise an error with a message of its own, and raising it inside the migration's transaction
    /// rolls back everything the migration had done.
    ///
    /// <para>The earlier migrations that refuse this way (<c>AddProviderIdentityColumns</c>,
    /// <c>AddMachines</c>, <c>RepointOperatingExpenseMachines</c>) carry their own copy, and are left
    /// as they shipped: an applied migration is not edited. A migration that uses this type relies on
    /// it never changing behaviour, so a change here is a new type, not an edit.</para>
    /// </summary>
    internal sealed class SqliteRefusalGuard
    {
        private readonly string _table;

        public SqliteRefusalGuard(string table) => _table = table;

        /// <summary>Creates the scratch table. Drop it again once the checks have passed.</summary>
        public void Create(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""CREATE TABLE "{_table}" ("Offending" INTEGER NOT NULL);""");

        public void Drop(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql($"""DROP TABLE "{_table}";""");

        /// <summary>
        /// Aborts the migration with <paramref name="message"/> when <paramref name="countingSql"/>
        /// counts anything at all. The message is a literal because <c>RAISE</c> takes no
        /// expression; it should name the rule that was broken and no row data, because it reaches
        /// the startup log.
        /// </summary>
        public void RefuseWhenNotZero(MigrationBuilder migrationBuilder, string message, string countingSql)
        {
            var trigger = $"TR_{_table}_Refuse";
            var literal = message.Replace("'", "''", StringComparison.Ordinal);

            migrationBuilder.Sql(string.Join(
                "\n",
                $"""CREATE TRIGGER "{trigger}" BEFORE INSERT ON "{_table}" WHEN NEW."Offending" <> 0""",
                $"BEGIN SELECT RAISE(ABORT, '{literal}'); END;"));
            migrationBuilder.Sql($"""INSERT INTO "{_table}" ("Offending") {countingSql};""");
            migrationBuilder.Sql($"""DROP TRIGGER "{trigger}";""");
        }
    }
}
