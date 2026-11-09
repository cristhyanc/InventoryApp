using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the keyed menu-visibility table (issue #1148): one row per switchable Admin
    /// destination a Super Admin has switched, keyed by the destination's own catalogue key.
    ///
    /// <para><strong>Additive, and it copies exactly one value.</strong> Nothing is dropped and
    /// nothing is rewritten. <c>PlatformOptionVisibility</c> (issue #873) is left exactly as it is
    /// and is no longer written; the one fact it may hold that still matters is a stored
    /// <c>HistoricalGstClassificationVisible = 0</c>, so this migration carries that - and only
    /// that - over as the single row <c>admin.historical-gst-classification</c> = off, keeping the
    /// old row's own <c>UpdatedAtUtc</c> rather than inventing an instant. An installation whose
    /// row says <c>1</c>, and one with no row at all, get <strong>no row here</strong>: a missing
    /// row means On, so both keep every destination offered, which is exactly the visibility they
    /// have today.</para>
    ///
    /// <para>There is nothing else to backfill and no financial, inventory or GST data in reach:
    /// the table holds one boolean per destination about what a menu offers. Every other catalogue
    /// key starts with no row, which is the state every installation is in until a Super Admin
    /// switches something off.</para>
    ///
    /// <para><strong>The key is not generated.</strong> A row's key is the catalogue key the write
    /// named, so two writes to the same destination collide on this primary key instead of leaving
    /// two rows with two different answers, while writes to different destinations are different
    /// rows that cannot overwrite each other.</para>
    ///
    /// <para><c>Down</c> drops the table, which loses only this switch's own state - and not even
    /// the GST answer, which <c>PlatformOptionVisibility</c> still holds, so reapplying this
    /// migration carries it over again.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddPlatformMenuVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformMenuVisibility",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Visible = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMenuVisibility", x => x.Key);
                });

            // The one carried-over value: a stored "Historical GST Classification is switched off"
            // keeps that destination switched off under its new key, with the instant the old row
            // recorded. A stored 1, and no row at all, insert nothing - a missing row means On.
            migrationBuilder.Sql("""
                INSERT INTO "PlatformMenuVisibility" ("Key", "Visible", "UpdatedAtUtc")
                SELECT 'admin.historical-gst-classification', 0, "UpdatedAtUtc"
                FROM "PlatformOptionVisibility"
                WHERE "HistoricalGstClassificationVisible" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformMenuVisibility");
        }
    }
}
