using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (issue #1077).
    ///
    /// <para>It carries no <c>BuildTargetModel</c>, for the same reason as
    /// <c>20261103090000_AddSupplierPresetSearchPrices</c> and
    /// <c>20261101090000_SetWeeklySupplierPriceFreshness</c>: this migration was written by hand, so
    /// there is no generated point-in-time model to record, and a hand-copied one would be a second
    /// model that could silently disagree with the real one. <c>AppDbContextModelSnapshot</c> is the
    /// authoritative target model and was updated with this migration - the two link entities were
    /// removed from it - which is what <c>MigrationRelocationTests</c>' pending-model-changes
    /// assertion compares the live model against. Both directions of this migration are written as
    /// operations that need no model at all.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261104090000_DropProviderIdentityLinkTables")]
    public partial class DropProviderIdentityLinkTables
    {
    }
}
