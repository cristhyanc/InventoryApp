using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Records, per business, whether the last completed Nayax connection check left the
    /// machine-scoped checks unverified (issue #537, a slice of #506).
    ///
    /// Purely additive: one new column on <c>BusinessNayaxConnections</c>, with no index, no
    /// constraint and nothing else touched. Existing rows take the <c>false</c> default and
    /// <strong>nothing is backfilled</strong> - a connection no check has ever run against reports
    /// "not pending", because the flag exists to ask an Owner to re-test once their machines are set
    /// up, not to make a connection that is working in production look incomplete. No credential,
    /// status, revision or last-tested instant is read or changed by this migration.
    /// </summary>
    public partial class AddNayaxConnectionMachineChecksPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MachineChecksPending",
                table: "BusinessNayaxConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MachineChecksPending",
                table: "BusinessNayaxConnections");
        }
    }
}
