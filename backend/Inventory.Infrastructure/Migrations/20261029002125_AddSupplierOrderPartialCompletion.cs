using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records who completed a supplier order with part of it undelivered, and when (issue #974):
    /// three additive nullable columns on <c>SupplierOrders</c>.
    ///
    /// <para><strong>Nothing is backfilled and nothing is reclassified, deliberately.</strong> Every
    /// order that already exists reads <c>NULL</c> on all three, which is the truth: nobody made
    /// that decision about it. The decision's own state is the new <c>Status</c> value <c>4</c>
    /// (<c>ClosedPartiallyDelivered</c>), and no existing <c>Ordered</c>, <c>PartiallyReceived</c>,
    /// <c>Received</c> or <c>Cancelled</c> row is moved into it - a partially received order stays
    /// partially received until a person decides otherwise. The status column needs no schema change
    /// at all: it is already an integer, for the reason <c>Businesses.Status</c> and
    /// <c>BusinessMemberships.Role</c> carry no <c>CHECK</c> constraint either.</para>
    ///
    /// <para>The actor is the validated Entra <c>(tid, oid)</c> pair, following
    /// <c>InventoryCostRepairs.CreatedByDirectoryTenantId</c>/<c>CreatedByObjectId</c>: no email
    /// address and no display name, because those are mutable, reassignable personal data. They are
    /// nullable rather than required because the overwhelming majority of rows legitimately have no
    /// value; the write path sets all three together or none, and the column pair is never read as
    /// an identity for anything but the audit trail.</para>
    ///
    /// <para><strong>Why <c>Down</c> uses <c>ALTER TABLE ... DROP COLUMN</c>.</strong> The same
    /// reason <c>AddBusinessLifecycleStatusAndProfile</c> does, plus one this migration cannot avoid.
    /// SQLite has dropped an unindexed, unconstrained column in place since 3.35, which is what the
    /// bundled library this application runs on provides, and <c>SupplierOrders</c> is the table the
    /// order lines, the receipt allocations and therefore every recorded delivery hang off: rebuilding
    /// it - create a copy, move every row, drop the original, rename - is risk a rollback of three
    /// nullable audit columns does not need. The reason it is not merely preferable here is that the
    /// provider's <c>DropColumn</c> <em>cannot</em> rebuild without a model, and the model it would
    /// rebuild from is the <em>preceding</em> migration's recorded one: several migrations in this
    /// history are hand-written and record none at all (<c>AddPriceCollectorResults</c> says so in
    /// its own designer file), so whichever one ends up in front of this migration would decide
    /// whether rolling back works. The in-place form needs no model and so does not care.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddSupplierOrderPartialCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletedByDirectoryTenantId",
                table: "SupplierOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedByObjectId",
                table: "SupplierOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedWithUndeliveredAt",
                table: "SupplierOrders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // In place, so the table every order line and receipt allocation refers to is never
            // dropped and rebuilt, and so the rollback needs no point-in-time model at all. The
            // orders, their lines, their allocations and their indexes are untouched; only the three
            // columns and the values in them go, which is what a rollback of this migration means.
            migrationBuilder.Sql(
                "ALTER TABLE \"SupplierOrders\" DROP COLUMN \"CompletedByDirectoryTenantId\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"SupplierOrders\" DROP COLUMN \"CompletedByObjectId\";");
            migrationBuilder.Sql(
                "ALTER TABLE \"SupplierOrders\" DROP COLUMN \"CompletedWithUndeliveredAt\";");
        }
    }
}
