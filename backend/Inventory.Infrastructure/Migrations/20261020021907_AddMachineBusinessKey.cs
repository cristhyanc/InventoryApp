using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// Declares <c>(BusinessId, Id)</c> on <c>Machines</c> (issue #780, parent #329 child G2a): the
    /// principal side every reference to a machine points at.
    ///
    /// <para><strong>Purely a constraint - no data changes and no column does.</strong> It states
    /// "(business, local id) identifies one machine", which is what lets a tenant-owned row name a
    /// machine with a foreign key that cannot cross a business, the same shape
    /// <c>AddProductAndCategoryBusinessKeys</c> gave <c>Products</c> and <c>Categories</c>. SQLite
    /// has no <c>ADD CONSTRAINT</c>, so EF Core rebuilds the table and copies every row; the
    /// machines' own ids are copied unchanged.</para>
    ///
    /// <para><strong>Why it is its own migration.</strong> The foreign key that uses it
    /// (<c>RepointOperatingExpenseMachines</c>) rebuilds <c>OperatingExpenses</c>, and within one
    /// migration EF Core creates both replacement tables before renaming either - so the new
    /// <c>OperatingExpenses</c> would reference a <c>Machines</c> that does not carry this
    /// constraint yet, and SQLite rejects that as a foreign key mismatch. Splitting them lets the
    /// rebuild of <c>Machines</c> complete first.</para>
    /// </summary>
    public partial class AddMachineBusinessKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Machines_BusinessId_Id",
                table: "Machines",
                columns: new[] { "BusinessId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_Machines_BusinessId_Id",
                table: "Machines");
        }
    }
}
