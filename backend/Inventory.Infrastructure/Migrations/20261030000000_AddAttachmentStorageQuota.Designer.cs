using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (issue #968).
    ///
    /// <para><strong>It carries no <c>BuildTargetModel</c>, and that is deliberate rather than
    /// missing</strong> - the same decision, for the same reason, as
    /// <c>20261029120000_AllowPurchaseWithoutDocument</c> and
    /// <c>20261028120000_AddPriceCollectorResults</c>. The authoritative target model is
    /// <c>AppDbContextModelSnapshot</c>, which was updated with this migration and is what
    /// <c>MigrationRelocationTests</c>' pending-model-changes assertion compares the live model
    /// against; a hand-copied point-in-time model would be a second model that could silently
    /// disagree with the real one. Nothing at runtime reads a migration's target model: a deployment
    /// applies migrations by <c>MigrationId</c> and runs their operations, and both of this
    /// migration's directions are a created table, an added column and two indexes that need no
    /// model at all.</para>
    ///
    /// <para><strong>The id is later than the migration it follows</strong>
    /// (<c>20261029120000_AllowPurchaseWithoutDocument</c>) rather than the clock time it was
    /// generated at, because migration order <em>is</em> the id: an id that sorted before an already
    /// released migration would make "migrate to AllowPurchaseWithoutDocument" apply this one too,
    /// which <c>AddAttachmentStorageQuotaMigrationTests</c> is what noticed.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261030000000_AddAttachmentStorageQuota")]
    public partial class AddAttachmentStorageQuota
    {
    }
}
