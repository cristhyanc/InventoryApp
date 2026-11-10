using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (issue #1162).
    ///
    /// <para><strong>It carries no <c>BuildTargetModel</c>, and that is deliberate rather than
    /// missing</strong>, for the reason <c>20261028120000_AddPriceCollectorResults</c> sets out: this
    /// migration was written by hand - the EF Core design-time tool could not be run where it was
    /// authored - so there is no generated point-in-time model to record here, and a hand-copied one
    /// would be a second model that could silently disagree with the real one. The authoritative
    /// target model is <c>AppDbContextModelSnapshot</c>, which was updated with this migration and is
    /// what <c>MigrationRelocationTests</c>' pending-model-changes assertion compares the live model
    /// against. Neither direction of this migration needs a point-in-time model: <c>Up</c> adds a
    /// nullable column in place and <c>Down</c> drops it with one SQLite statement.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261110000000_AddPurchaseInvoiceNumber")]
    public partial class AddPurchaseInvoiceNumber
    {
    }
}
