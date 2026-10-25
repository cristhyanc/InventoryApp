using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the platform supplier presets of issue #848 and inserts the four suggestions the
    /// platform ships with - Coles, Woolworths, Big W and Costco - once.
    ///
    /// <para><strong>Why the rows are inserted here rather than seeded.</strong> EF Core's
    /// <c>HasData</c> and a startup seeding step both re-apply themselves: the first emits
    /// <c>UpdateData</c>/<c>InsertData</c> from every later migration that touches the model, and the
    /// second runs on every restart. Production applies migrations at startup (AGENTS.md § Database
    /// and migrations), so either one would quietly undo a Super Admin's edit, or re-enable a preset
    /// they deliberately disabled, on the next deployment. A one-off <c>InsertData</c> in this
    /// migration runs exactly once, when this migration is applied, and never again - which is what
    /// makes an edit to a shipped row a permanent edit.</para>
    ///
    /// <para>The instants are a fixed literal rather than the clock, so applying this migration
    /// produces the same four rows on every database it is applied to.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPresets : Migration
    {
        /// <summary>
        /// When the shipped rows are recorded as having been created. A literal, not
        /// <c>DateTime.UtcNow</c>: a migration that wrote the clock would leave two databases of the
        /// same release holding different data.
        /// </summary>
        private static readonly DateTime SeededAtUtc = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierPresets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalisedName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPresets", x => x.Id);
                });

            // Name uniqueness ignoring case, enforced by the database over the stored normalised
            // name rather than by a read-then-insert: two concurrent creates of the same name can
            // both pass a WHERE clause, and only one of them can win this index.
            migrationBuilder.CreateIndex(
                name: "IX_SupplierPresets_NormalisedName",
                table: "SupplierPresets",
                column: "NormalisedName",
                unique: true);

            // The four shipped suggestions, enabled, with no website recorded: issue #848 lists the
            // names and nothing else, and a URL nobody verified is not something a migration should
            // invent. Each NormalisedName is SupplierPresetPolicy.NormalisedName of the name beside
            // it - the trimmed name upper-cased invariantly - written out here because a migration
            // states the rows it inserts rather than calling into application code that may change.
            migrationBuilder.InsertData(
                table: "SupplierPresets",
                columns: new[] { "Name", "NormalisedName", "Url", "IsEnabled", "CreatedAtUtc", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { "Coles", "COLES", null, true, SeededAtUtc, SeededAtUtc },
                    { "Woolworths", "WOOLWORTHS", null, true, SeededAtUtc, SeededAtUtc },
                    { "Big W", "BIG W", null, true, SeededAtUtc, SeededAtUtc },
                    { "Costco", "COSTCO", null, true, SeededAtUtc, SeededAtUtc }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes the seeded rows and every edit to them with it. That is what
            // reversing this migration means - the table did not exist before it - and it is the
            // other half of why the rows are inserted here rather than treated as configuration
            // something else can restore.
            migrationBuilder.DropTable(
                name: "SupplierPresets");
        }
    }
}
