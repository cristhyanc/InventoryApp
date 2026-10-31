using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Makes the platform supplier presets the supplier <em>master</em> for price checks (issue #931,
    /// first task of #817): five additive columns saying which states a supplier serves, whether its
    /// published prices include GST and how those prices are collected, the values for the four rows
    /// <c>AddSupplierPresets</c> created, and the five records the first rollout checks and the list
    /// was missing.
    ///
    /// <para><strong>The rows are inserted here, exactly as #848 inserted its four, and for the same
    /// reason.</strong> EF Core's <c>HasData</c> and a startup seeding step both re-apply themselves,
    /// and production applies migrations at startup (AGENTS.md § Database and migrations), so either
    /// would quietly undo a Super Admin's edit or re-enable a preset they deliberately disabled on the
    /// next deployment. These five one-off <c>INSERT</c> statements run once, when this migration is
    /// applied, and never again - which is what makes an edit to a shipped row a permanent edit.</para>
    ///
    /// <para><strong>Each insert is skipped when that normalised name is already taken.</strong> The
    /// Super Admin surface of #848 has been able to create a preset by any name since before this
    /// migration existed, so a database being upgraded may already hold an <c>ALDI</c>, a
    /// <c>KMART</c> or a JB Metro record somebody added by hand. <c>NormalisedName</c> is unique, so
    /// an unconditional insert would fail with <c>UNIQUE constraint failed</c> - and because
    /// production applies migrations at startup and fails closed, that failure would stop the
    /// application from starting. Every insert is therefore written as
    /// <c>INSERT ... SELECT ... WHERE NOT EXISTS</c> over that name: a database without the record
    /// gets it, a database that already has one keeps the record it has, exactly as it is. That row
    /// is <em>not</em> given the values below either - its name, website, enabled state and both
    /// instants stay untouched, and its new columns take the fail-closed defaults, because a preset
    /// somebody else created is not a preset this migration may re-point at a collection route or
    /// declare GST-inclusive on their behalf. It reads as "no market recorded", which is visible on
    /// the page and refused by <c>SupplierPresetMasterPolicy</c> the moment it is edited.</para>
    ///
    /// <para><strong>The four existing rows are recognised by their normalised name, and nothing
    /// else.</strong> Each <c>UPDATE</c> is written out rather than expressed as an
    /// <c>UpdateData</c> keyed on a row id, because the ids are whatever the database assigned when
    /// #848 was applied and are not the same on two databases. A row that was renamed is not a row
    /// this migration knows: it matches no <c>WHERE</c> clause here and is left entirely alone,
    /// keeping the column defaults, because guessing that "Coles Group" is still Coles would be a
    /// migration overwriting a decision somebody made deliberately. A row that was deleted matches
    /// nothing either, and a row that was disabled is updated without being re-enabled - what a
    /// preset claims and whether it is suggested are different facts, and this migration decides only
    /// the first.</para>
    ///
    /// <para><strong>The defaults fail closed.</strong> <c>ServesNationwide</c> and
    /// <c>PricesIncludeGst</c> default to <c>0</c> and <c>ServedStates</c> to the empty string, so a
    /// row this migration does not recognise reads as "no market recorded" - an unconfigured preset a
    /// Super Admin must complete, which is visible on the page and refused by
    /// <c>SupplierPresetMasterPolicy</c> the moment it is edited. Defaulting to nationwide would have
    /// been this migration inventing a claim about where a supplier sells, and defaulting
    /// <c>PricesIncludeGst</c> to <c>1</c> would have been it inventing a tax basis. The connector and
    /// its key are nullable with no default at all, because "nothing collects this supplier's prices"
    /// is the real state Costco is in rather than a value anybody chose.</para>
    ///
    /// <para><strong>The instants are untouched on the four rows that already existed.</strong> This
    /// migration gave them columns they never had; it did not make an edit on anybody's behalf, and
    /// moving <c>UpdatedAtUtc</c> would have the Super Admin's own "last changed" column report a
    /// change no person made. The five inserted records carry a fixed literal instant rather than the
    /// clock, so applying this migration produces the same rows on every database - and that literal
    /// is also what tells <c>Down</c> which rows this migration created.</para>
    ///
    /// <para><strong>The connector numbers are written out as literals.</strong> They are
    /// <c>Inventory.Domain.Suppliers.SupplierPriceConnector</c>'s declared values - <c>1</c>
    /// Varnmill, <c>2</c> Kmart, <c>3</c> BigW, <c>4</c> JbMetro - stated here rather than taken from
    /// the enum, because a migration records the rows it inserted and must not change meaning when
    /// application code does. <c>AddSupplierPresetMasterFieldsMigrationTests</c> asserts the stored
    /// numbers against that enum, so renumbering it fails the build rather than silently re-pointing
    /// nine seeded records at the wrong collection routes.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPresetMasterFields : Migration
    {
        /// <summary>
        /// When the five inserted records are recorded as having been created, in the text form EF
        /// Core's SQLite provider stores a <c>DateTime</c> in. A literal, not <c>DateTime.UtcNow</c>:
        /// a migration that wrote the clock would leave two databases of the same release holding
        /// different data.
        ///
        /// <para>It is also the provenance stamp <c>Down</c> recognises. Every row this migration
        /// inserts carries exactly this instant as its <c>CreatedAtUtc</c>, and a preset a person
        /// created carries the clock instant of the moment they created it, to the tick - so "created
        /// at this exact literal" means "inserted by this migration" and nothing else.</para>
        /// </summary>
        private const string SeededAtUtc = "2026-10-27 00:00:00";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PriceConnector",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceConnectorKey",
                table: "SupplierPresets",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PricesIncludeGst",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ServedStates",
                table: "SupplierPresets",
                type: "TEXT",
                maxLength: 28,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ServesNationwide",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The three supermarkets and BIG W take a collection route; Coles's prices are national
            // (the owner's decision of 27 October 2026), and so are the other three retailers'. Each
            // statement names one normalised name and updates at most one row, or none at all when
            // that row was renamed or removed.
            UpdateExistingPreset(
                migrationBuilder,
                normalisedName: "COLES",
                servesNationwide: true,
                pricesIncludeGst: true,
                priceConnector: "1",
                priceConnectorKey: "'coles'");

            UpdateExistingPreset(
                migrationBuilder,
                normalisedName: "WOOLWORTHS",
                servesNationwide: true,
                pricesIncludeGst: true,
                priceConnector: "1",
                priceConnectorKey: "'woolworths'");

            UpdateExistingPreset(
                migrationBuilder,
                normalisedName: "BIG W",
                servesNationwide: true,
                pricesIncludeGst: true,
                priceConnector: "3",
                priceConnectorKey: "NULL");

            // Costco stays on the list with no connector until the owner decides otherwise, so its
            // prices are collected by nobody - which is a recorded state and not a gap.
            UpdateExistingPreset(
                migrationBuilder,
                normalisedName: "COSTCO",
                servesNationwide: true,
                pricesIncludeGst: true,
                priceConnector: "NULL",
                priceConnectorKey: "NULL");

            // The five records the list was missing. Each NormalisedName is
            // SupplierPresetPolicy.NormalisedName of the name beside it - the trimmed name upper-cased
            // invariantly - written out here because a migration states the rows it inserts rather
            // than calling into application code that may change.
            //
            // Each statement inserts nothing when a preset already holds that normalised name: the
            // unique index would otherwise fail the whole upgrade, and a record somebody created
            // themselves is theirs to keep (see the class remarks).
            //
            // The three JB Metro records are one supplier per site: the JbMetro route reads the site
            // at the record's own Url, which is why a new JB Metro site is a new record and not new
            // code, and why all three share one route with no key between them. Their prices are
            // ex-GST, unlike every other record here.
            InsertPresetUnlessTheNameIsTaken(
                migrationBuilder,
                normalisedName: "ALDI",
                values: $"'ALDI', 'ALDI', NULL, 1, '{SeededAtUtc}', '{SeededAtUtc}', 1, '', 1, 1, 'aldi'");

            InsertPresetUnlessTheNameIsTaken(
                migrationBuilder,
                normalisedName: "KMART",
                values: $"'Kmart', 'KMART', NULL, 1, '{SeededAtUtc}', '{SeededAtUtc}', 1, '', 1, 2, NULL");

            InsertPresetUnlessTheNameIsTaken(
                migrationBuilder,
                normalisedName: "JB METRO NSW",
                values: $"'JB Metro NSW', 'JB METRO NSW', 'https://www.jbmetro.com.au', 1, "
                    + $"'{SeededAtUtc}', '{SeededAtUtc}', 0, 'NSW', 0, 4, NULL");

            InsertPresetUnlessTheNameIsTaken(
                migrationBuilder,
                normalisedName: "JB METRO SOUTH COAST & ACT",
                values: $"'JB Metro South Coast & ACT', 'JB METRO SOUTH COAST & ACT', "
                    + $"'https://www.jbmetro-sc-act.com.au', 1, '{SeededAtUtc}', '{SeededAtUtc}', "
                    + "0, 'NSW,ACT', 0, 4, NULL");

            InsertPresetUnlessTheNameIsTaken(
                migrationBuilder,
                normalisedName: "JB METRO SA & NT",
                values: $"'JB Metro SA & NT', 'JB METRO SA & NT', "
                    + $"'https://www.jbmetro-sa-nt.com.au', 1, '{SeededAtUtc}', '{SeededAtUtc}', "
                    + "0, 'SA,NT', 0, 4, NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The records this migration inserted go, so that reversing it leaves the list #848
            // shipped again - and nothing else goes with them.
            //
            // A row qualifies only when it holds one of the five normalised names *and* carries the
            // literal CreatedAtUtc Up stamps on the rows it creates. Deleting by name alone would
            // remove a preset a Super Admin had created themselves under one of these names before
            // this migration ever ran - exactly the record Up was careful not to touch - and a
            // delete is not recoverable by reversing anything. A record somebody renamed in between
            // matches no name here and stays, for the same reason a renamed row is not updated above:
            // it is no longer a row this migration recognises.
            DeleteInsertedPreset(migrationBuilder, "ALDI");
            DeleteInsertedPreset(migrationBuilder, "KMART");
            DeleteInsertedPreset(migrationBuilder, "JB METRO NSW");
            DeleteInsertedPreset(migrationBuilder, "JB METRO SOUTH COAST & ACT");
            DeleteInsertedPreset(migrationBuilder, "JB METRO SA & NT");

            // The values set on the four existing rows are not restored to anything: they go with the
            // columns that held them, which is what reversing an additive migration means. Everything
            // those rows carried before - name, website, enabled state and both instants - is
            // untouched, because this migration never wrote them.
            migrationBuilder.DropColumn(
                name: "PriceConnector",
                table: "SupplierPresets");

            migrationBuilder.DropColumn(
                name: "PriceConnectorKey",
                table: "SupplierPresets");

            migrationBuilder.DropColumn(
                name: "PricesIncludeGst",
                table: "SupplierPresets");

            migrationBuilder.DropColumn(
                name: "ServedStates",
                table: "SupplierPresets");

            migrationBuilder.DropColumn(
                name: "ServesNationwide",
                table: "SupplierPresets");
        }

        /// <summary>
        /// Sets the five new columns on the one row holding <paramref name="normalisedName"/>, if it
        /// is still there under that name.
        ///
        /// <para>Every one of the four shipped suppliers is nationwide and publishes GST-inclusive
        /// prices, so <c>ServedStates</c> is always cleared here; the parameters are still spelled out
        /// per row rather than assumed, because what each row claims is data this migration is
        /// recording and not a pattern it is applying. <c>IsEnabled</c>, <c>Name</c>, <c>Url</c>,
        /// <c>CreatedAtUtc</c> and <c>UpdatedAtUtc</c> are deliberately absent from the <c>SET</c>
        /// list: this migration writes only the columns it created.</para>
        /// </summary>
        private static void UpdateExistingPreset(
            MigrationBuilder migrationBuilder,
            string normalisedName,
            bool servesNationwide,
            bool pricesIncludeGst,
            string priceConnector,
            string priceConnectorKey) =>
            migrationBuilder.Sql(
                $"""
                UPDATE SupplierPresets
                SET ServesNationwide = {(servesNationwide ? 1 : 0)},
                    ServedStates = '',
                    PricesIncludeGst = {(pricesIncludeGst ? 1 : 0)},
                    PriceConnector = {priceConnector},
                    PriceConnectorKey = {priceConnectorKey}
                WHERE NormalisedName = '{normalisedName}';
                """);

        /// <summary>
        /// Inserts one of the five records, unless a preset already holds
        /// <paramref name="normalisedName"/>.
        ///
        /// <para>The <c>WHERE NOT EXISTS</c> is the whole point: <c>NormalisedName</c> is unique, the
        /// Super Admin surface has been able to create any name since #848, and production applies
        /// migrations at startup and fails closed - so an unconditional insert over a database that
        /// already holds an <c>ALDI</c> would stop the application from starting. The existing row is
        /// then left exactly as it is, values and instants included.</para>
        ///
        /// <para><paramref name="values"/> is the record's eleven column values as SQL literals, in
        /// the order of the column list below. They are written out at each call site rather than
        /// assembled from parameters, because the rows this migration inserts are data it records and
        /// not a pattern it applies.</para>
        /// </summary>
        private static void InsertPresetUnlessTheNameIsTaken(
            MigrationBuilder migrationBuilder,
            string normalisedName,
            string values) =>
            migrationBuilder.Sql(
                $"""
                INSERT INTO SupplierPresets
                    (Name, NormalisedName, Url, IsEnabled, CreatedAtUtc, UpdatedAtUtc,
                     ServesNationwide, ServedStates, PricesIncludeGst, PriceConnector, PriceConnectorKey)
                SELECT {values}
                WHERE NOT EXISTS (
                    SELECT 1 FROM SupplierPresets WHERE NormalisedName = '{normalisedName}');
                """);

        /// <summary>
        /// Removes the row <see cref="Up"/> inserted under <paramref name="normalisedName"/>, and
        /// only that row: a preset that already held the name when this migration ran carries its
        /// creator's own instant and is not matched here.
        /// </summary>
        private static void DeleteInsertedPreset(
            MigrationBuilder migrationBuilder,
            string normalisedName) =>
            migrationBuilder.Sql(
                $"""
                DELETE FROM SupplierPresets
                WHERE NormalisedName = '{normalisedName}'
                  AND CreatedAtUtc = '{SeededAtUtc}';
                """);
    }
}
