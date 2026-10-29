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
            migrationBuilder.DropColumn(
                name: "CompletedByDirectoryTenantId",
                table: "SupplierOrders");

            migrationBuilder.DropColumn(
                name: "CompletedByObjectId",
                table: "SupplierOrders");

            migrationBuilder.DropColumn(
                name: "CompletedWithUndeliveredAt",
                table: "SupplierOrders");
        }
    }
}
