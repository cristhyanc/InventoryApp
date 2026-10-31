using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Links a business's supplier to the platform supplier preset it was copied from, and gives it
    /// a "Not used" switch (issue #934, second task of #817).
    ///
    /// <para><strong>Additive, in this order:</strong> the two columns, a one-time name-based
    /// backfill that writes only the new link column, and then the filtered unique index over
    /// <c>(BusinessId, SupplierPresetId)</c>. The order is the point - the index is added
    /// <em>after</em> the backfill, over data the backfill has already made safe for it, so the
    /// upgrade cannot fail on an existing database and no row ever has to be deleted, unlinked or
    /// merged to make an index fit. The foreign key to <c>SupplierPresets</c> is <c>Restrict</c>, so
    /// nothing is deleted or cascaded by it either.</para>
    ///
    /// <para><strong>Why a name is consulted at all, this once.</strong> Issue #849 copies the
    /// enabled presets into a business when its application is approved, and deliberately never
    /// looks at supplier names: a business renames and tidies its own suppliers, so a name proves
    /// nothing about whether a copy was made. That rule is unchanged for the copying. This migration
    /// is the one deliberate exception the owner decided on 27 October 2026, and it is narrower in
    /// every direction: it runs once, it reads a name only to decide a <em>link</em>, and it creates
    /// nothing, renames nothing, hides nothing and deletes nothing. A business approved before this
    /// release holds copies that carry no link, and the alternative - leaving them unlinked forever -
    /// would mean the price comparison could never say which supplier a stored price belongs to.</para>
    ///
    /// <para><strong>The duplicate rule, which is what makes the index safe.</strong> A supplier is
    /// linked only when its normalised name matches a preset's <c>NormalisedName</c> <em>and</em> no
    /// other supplier of the same business normalises to the same value. Two rows that both match
    /// one preset - "Coles" and " COLES " in one business - are therefore <strong>both</strong> left
    /// unlinked, because this migration cannot know which of them the business actually buys from,
    /// and linking either would be a guess that the unique index would then make permanent. Another
    /// business's single "Coles" is linked as normal: the rule is per business, exactly as the index
    /// is. A supplier matching no preset is left unlinked too. Either way the row itself is
    /// untouched - same name, same website, same contact details, same GST defaults, not hidden.</para>
    ///
    /// <para><strong>What "the same name" means here, and what it deliberately does not.</strong>
    /// The comparison is SQLite's <c>UPPER(TRIM(Name))</c> against the stored
    /// <c>SupplierPresets.NormalisedName</c>, which <c>SupplierPresetPolicy.NormalisedName</c> wrote
    /// as .NET's <c>Trim().ToUpperInvariant()</c>. The two agree on every name either side can
    /// actually hold today, and they differ only outside ASCII: SQLite's <c>UPPER</c> folds ASCII
    /// letters and leaves "café" alone, and its <c>TRIM</c> removes spaces rather than every Unicode
    /// whitespace character. A name where they disagree simply does not match, so it is left
    /// unlinked - the fail-closed direction, and a supplier a Super Admin can still be asked about.
    /// The alternative, re-implementing invariant upper-casing in SQL, would be a second normalising
    /// rule that could drift from the one the presets are indexed by.</para>
    ///
    /// <para><strong>Reversing it.</strong> <c>Down</c> drops the index, the key and the two columns,
    /// which takes the links with them. Nothing restores a name, a website or a hidden flag, because
    /// <c>Up</c> never wrote one.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPresetLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "Suppliers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SupplierPresetId",
                table: "Suppliers",
                type: "INTEGER",
                nullable: true);

            BackfillLinksByName(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BusinessId_SupplierPresetId",
                table: "Suppliers",
                columns: new[] { "BusinessId", "SupplierPresetId" },
                unique: true,
                filter: "\"SupplierPresetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_SupplierPresetId",
                table: "Suppliers",
                column: "SupplierPresetId");

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_SupplierPresets_SupplierPresetId",
                table: "Suppliers",
                column: "SupplierPresetId",
                principalTable: "SupplierPresets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Suppliers_SupplierPresets_SupplierPresetId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BusinessId_SupplierPresetId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_SupplierPresetId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "SupplierPresetId",
                table: "Suppliers");
        }

        /// <summary>
        /// Links each supplier whose normalised name matches exactly one preset and is the only
        /// supplier of its business with that normalised name.
        ///
        /// <para>One statement, so there is no moment at which half the links exist. The
        /// <c>SET</c> clause and the first <c>WHERE</c> clause run the same lookup, because SQLite
        /// has no <c>UPDATE ... FROM</c> in every version this application has to upgrade and an
        /// unguarded correlated subquery would write <c>NULL</c> over the rows it did not match -
        /// harmless on a column that is <c>NULL</c> everywhere today, but a rule that only works
        /// while that is true. <c>SupplierPresets.NormalisedName</c> is uniquely indexed, so the
        /// subquery returns at most one row by construction.</para>
        ///
        /// <para>The second <c>WHERE</c> clause is the duplicate rule, and it compares suppliers
        /// to each other rather than to presets: within one business, a second row normalising to
        /// the same name leaves <em>both</em> unlinked. It is written over
        /// <c>UPPER(TRIM(...))</c> on both sides so "Coles" and " COLES " are recognised as the
        /// pair they are.</para>
        /// </summary>
        private static void BackfillLinksByName(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                UPDATE Suppliers
                SET SupplierPresetId = (
                    SELECT preset.Id
                    FROM SupplierPresets preset
                    WHERE preset.NormalisedName = UPPER(TRIM(Suppliers.Name)))
                WHERE EXISTS (
                    SELECT 1
                    FROM SupplierPresets preset
                    WHERE preset.NormalisedName = UPPER(TRIM(Suppliers.Name)))
                  AND NOT EXISTS (
                    SELECT 1
                    FROM Suppliers other
                    WHERE other.BusinessId = Suppliers.BusinessId
                      AND other.Id <> Suppliers.Id
                      AND UPPER(TRIM(other.Name)) = UPPER(TRIM(Suppliers.Name)));
                """);
    }
}
