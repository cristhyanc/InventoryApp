using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Gives a membership somewhere to keep the member's display name and email address (issue
    /// #524), so an Owner can see who their members are instead of a list of Entra object ids.
    ///
    /// Additive in the strong sense: two nullable columns, no table rebuilt, no row read or
    /// written, no index touched and nothing backfilled. Every existing membership keeps both
    /// values empty until that member's next request refreshes them from their own validated
    /// sign-in token - the only thing that ever writes them. Nothing is inferred from an existing
    /// row, because there is nothing in the database to infer a person's name from.
    ///
    /// This is the first personal data in this table. It is display-only: no authorization,
    /// membership, role or capability decision reads either column, neither is logged, and the
    /// platform-diagnostics surface excludes this table entirely (docs/architecture.md § Member
    /// display name and email).
    ///
    /// Reversible: <c>Down</c> drops both columns, which discards display data and nothing else -
    /// no membership, role or access changes with it.
    /// </summary>
    public partial class AddMemberDisplayNameAndEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "BusinessMemberships",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "BusinessMemberships",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "BusinessMemberships");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "BusinessMemberships");
        }
    }
}
