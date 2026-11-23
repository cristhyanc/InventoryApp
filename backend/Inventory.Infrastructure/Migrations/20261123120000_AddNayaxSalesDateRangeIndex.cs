using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The sales date-range index of issue #1287 (audit #1226, finding DB-02): one additive
    /// <c>CREATE INDEX</c> on <c>NayaxSales (BusinessId, MachineAuthorizationTime)</c>, with no data
    /// change and no backfill. On a large table it holds the write lock while it builds, which is why
    /// the pull request reports the measured duration at the audit's size.
    ///
    /// <para>It is written by hand rather than scaffolded, so the <c>[DbContext]</c> and
    /// <c>[Migration]</c> attributes EF Core discovers a migration by are declared here instead of in
    /// a generated <c>.Designer.cs</c> partial, and this migration carries no model snapshot of its
    /// own: <see cref="AppDbContextModelSnapshot"/> is updated with the new index and remains the one
    /// snapshot the model is compared against (<c>MigrationRelocationTests</c> asserts that the
    /// snapshot still matches the mapped model, so the next <c>migrations add</c> emits nothing
    /// unreviewed). A migration's own target model is used only to type schema operations that need
    /// the model, which <c>CreateIndex</c>/<c>DropIndex</c> do not.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261123120000_AddNayaxSalesDateRangeIndex")]
    public partial class AddNayaxSalesDateRangeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_NayaxSales_BusinessId_MachineAuthorizationTime",
                table: "NayaxSales",
                columns: new[] { "BusinessId", "MachineAuthorizationTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NayaxSales_BusinessId_MachineAuthorizationTime",
                table: "NayaxSales");
        }

        /// <summary>
        /// Deliberately empty: see the note above. The index this migration adds is generated from
        /// the operation alone, and the model snapshot - not a per-migration copy of it - is what
        /// every model comparison reads.
        /// </summary>
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
        }
    }
}
