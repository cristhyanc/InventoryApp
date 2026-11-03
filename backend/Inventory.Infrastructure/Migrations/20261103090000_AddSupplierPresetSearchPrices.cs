using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Separates "suggested to businesses" from "searched by the price collector" on the platform
    /// supplier presets (issue #1077, the owner's decision of 3 November 2026): one additive
    /// <c>SearchPrices</c> column on <c>SupplierPresets</c>, backfilled from <c>IsEnabled</c>.
    ///
    /// <para><strong>The backfill is what makes this a no-op on deploy.</strong> Until now
    /// <c>IsEnabled</c> answered both questions: it decided whether a preset was copied into a newly
    /// approved business and offered under "Available to add", <em>and</em> whether
    /// <c>PriceCollectorWorkListPolicy.IsSupplierDue</c> searched it. Copying that column into the
    /// new one means every existing record is searched exactly as often after this migration as
    /// before it - a supplier somebody had disabled stays unsearched, and every other one keeps being
    /// searched - so the first collector run after the upgrade issues the same work the last one
    /// before it did. Setting the column from its own default instead would have switched searching
    /// back on for a supplier somebody had deliberately disabled, which on the Kmart and BIG W routes
    /// is money.</para>
    ///
    /// <para><strong>The column default is <c>1</c>, and it is about new rows rather than existing
    /// ones.</strong> A preset a Super Admin adds from now on is created searchable, because a
    /// supplier added to this list is one somebody wants priced; the <c>UPDATE</c> below then
    /// overwrites that default on every row that already existed. The two are not in tension: the
    /// default decides what an <em>insert</em> that states no value gets, and the backfill decides
    /// what the rows already in the table hold.</para>
    ///
    /// <para><strong>Nothing else is touched, in either direction.</strong> No name, website,
    /// enabled state, market, GST basis, connector, freshness limit or instant is read or rewritten,
    /// no row is inserted or deleted, and nothing outside this one table is involved -
    /// <c>UpdatedAtUtc</c> in particular stays as it is, because this migration gave the presets a
    /// column they never had rather than making an edit on a Super Admin's behalf. No business data
    /// exists to touch: <c>SupplierPresets</c> is a platform table with no <c>BusinessId</c>, no
    /// query filter and no ownership stamp (docs/architecture.md § Tenant ownership), and this
    /// migration adds none of those.</para>
    ///
    /// <para><strong>Reversing it drops the column</strong>, which loses only the distinction this
    /// issue introduced: the preset's own enabled state, which is what the collector read before, is
    /// never written here and so comes back untouched.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierPresetSearchPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SearchPrices",
                table: "SupplierPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Every row that already existed keeps being searched exactly as it was, because
            // IsEnabled is what decided that until now. One statement over one table, with no WHERE
            // clause on purpose: this is about every record, and a record whose name or market this
            // application does not recognise is still a record whose searching must not change.
            migrationBuilder.Sql("UPDATE SupplierPresets SET SearchPrices = IsEnabled;");
        }

        /// <summary>
        /// Drops the column again.
        ///
        /// <para><strong>Why it is hand-written SQL.</strong> EF's SQLite generator answers
        /// <c>DropColumnOperation</c> with a model-driven table rebuild, which needs the point-in-time
        /// model of the <em>preceding</em> migration - and
        /// <c>20261101090000_SetWeeklySupplierPriceFreshness</c> deliberately records none, exactly as
        /// <c>AddAttachmentStorageQuota</c>'s <c>Down</c> describes. SQLite has supported
        /// <c>ALTER TABLE ... DROP COLUMN</c> since 3.35, this column is in no index, no unique
        /// constraint and no check constraint, so the statement below is both exact and independent of
        /// any recorded model. The table's own check constraint on
        /// <c>PriceCheckFreshnessHours</c> names a different column and is unaffected.</para>
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""SupplierPresets"" DROP COLUMN ""SearchPrices"";");
        }
    }
}
