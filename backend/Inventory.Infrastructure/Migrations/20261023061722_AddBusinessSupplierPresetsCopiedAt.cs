using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the marker that records when a business received its own copies of the platform supplier
    /// presets (issue #849, child C of #835): one additive nullable column on <c>Businesses</c>.
    ///
    /// <para><strong>Nothing is backfilled, deliberately.</strong> Every business that already exists
    /// stays <c>NULL</c>, which reads as "no copy was ever made" and is the truth: those businesses
    /// already hold the suppliers they use, and a migration that stamped an instant on them would
    /// claim an event that never happened - while one that inserted four suppliers into each of them
    /// would change a trading business's own data. A <c>NULL</c> marker on a business that is already
    /// <c>Active</c> therefore never becomes a copy either: only approving a <c>Pending</c>
    /// application copies, and an active business has no application left to approve.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddBusinessSupplierPresetsCopiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SupplierPresetsCopiedAtUtc",
                table: "Businesses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierPresetsCopiedAtUtc",
                table: "Businesses");
        }
    }
}
