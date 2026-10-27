using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the Australian state or territory a business trades in (issue #933, one slice of #817):
    /// one additive nullable column on <c>Businesses</c>, bounded at the length of the longest
    /// abbreviation.
    ///
    /// <para><strong>Nothing is backfilled, deliberately.</strong> Every business that already
    /// exists reads <c>NULL</c>, which is the truth: nobody has recorded a state for it. It is not
    /// derived from the business's time zone, which cannot tell New South Wales from the Australian
    /// Capital Territory - the reason this column exists at all - and not from an address either.
    /// The Owner records it in Business settings, where the existing legacy-profile prompt asks for
    /// it, and nothing is blocked while it is unset.</para>
    ///
    /// <para>The eight permitted values are not a <c>CHECK</c> constraint, for the reason
    /// <c>Businesses.Status</c> and <c>BusinessMemberships.Role</c> carry none: SQLite cannot add
    /// one to an existing table without rebuilding it, and this migration is deliberately additive
    /// on the one table that decides who may sign in. The vocabulary is enforced where it has to be
    /// anyway - <c>BusinessStateEnforcer</c> on <c>SaveChanges</c>, over every write path to a
    /// business alike.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddBusinessState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "Businesses",
                type: "TEXT",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "State",
                table: "Businesses");
        }
    }
}
