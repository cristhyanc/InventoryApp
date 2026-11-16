using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Lets a purchase record the idempotency key it was created under (issue #1247): two additive,
    /// nullable columns on <c>Receipts</c> - <c>IdempotencyKey</c> and the
    /// <c>IdempotencyFingerprint</c> of the request it stands for - and the per-business unique
    /// index that makes the key mean something.
    ///
    /// <para><strong>There is no backfill, and there is nothing to backfill from.</strong> A
    /// purchase recorded before this migration was created by a request that sent no key, so every
    /// existing row keeps <c>NULL</c> in both columns - which is exactly what "this purchase was not
    /// created under a key" means. Inventing one would claim a key for a request that never sent
    /// one.</para>
    ///
    /// <para><strong>The index is unique per business and filtered to the rows that carry a
    /// key.</strong> Per business because two businesses may legitimately choose the same string, and
    /// a global index over a value a <em>business</em> supplies would let whichever business writes
    /// it first silently deny it to every other one (AGENTS.md § Tenant ownership and data
    /// isolation). Filtered because a purchase created without a key must constrain nothing: without
    /// the filter the second keyless purchase of a business would be refused outright, since SQLite
    /// treats <c>NULL</c>s in a unique index as distinct only by accident of its own rules rather
    /// than by anything this application should depend on.</para>
    ///
    /// <para><strong>Why <c>ALTER TABLE ... DROP COLUMN</c> on the way back.</strong> Dropping the
    /// columns through the provider's own <c>DropColumn</c> would rebuild <c>Receipts</c> - the table
    /// every purchase line, receipt allocation and purchase-linked stock movement hangs off - to
    /// remove two nullable columns, and it would need a point-in-time model to rebuild from. SQLite
    /// has dropped such a column in place since 3.35, which is what the bundled library this
    /// application runs on provides, exactly as <c>AddPurchaseInvoiceNumber</c> already does. The
    /// index is dropped first, because a column an index names cannot be dropped in place. Rolling
    /// back loses the recorded keys, which is inherent: the older schema has nowhere to keep them,
    /// and a retry of a create made before the rollback would then save a second purchase.</para>
    /// </summary>
    public partial class AddPurchaseIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyFingerprint",
                table: "Receipts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Receipts",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_BusinessId_IdempotencyKey",
                table: "Receipts",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipts_BusinessId_IdempotencyKey",
                table: "Receipts");

            migrationBuilder.Sql("ALTER TABLE \"Receipts\" DROP COLUMN \"IdempotencyKey\";");
            migrationBuilder.Sql("ALTER TABLE \"Receipts\" DROP COLUMN \"IdempotencyFingerprint\";");
        }
    }
}
