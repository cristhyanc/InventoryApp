using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the single shared option-visibility row's table (issue #873): whether Historical GST
    /// Classification is offered to anybody but a Super Admin.
    ///
    /// <para><strong>Additive, and deliberately empty.</strong> It inserts nothing at all: no row
    /// means every option is offered, so every existing installation keeps exactly the visibility it
    /// has today until a Super Admin switches something off. There is nothing to backfill, nothing to
    /// infer, and no financial, inventory or GST data in reach - the table holds one boolean about
    /// what a menu offers.</para>
    ///
    /// <para><strong>The key is not generated.</strong> The row's id is the fixed constant
    /// <c>PlatformOptionVisibility.SingletonId</c>, written by the adapter, so two writes that both
    /// find no row collide on this primary key instead of leaving two rows with two different
    /// answers.</para>
    ///
    /// <para><c>Down</c> drops the table, which loses only the switch's own state: the option returns
    /// to being offered, which is the state the application shipped with.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddPlatformOptionVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformOptionVisibility",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    HistoricalGstClassificationVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformOptionVisibility", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformOptionVisibility");
        }
    }
}
