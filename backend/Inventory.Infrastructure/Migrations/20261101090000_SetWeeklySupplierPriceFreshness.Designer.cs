using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <summary>
    /// The migration id and the context this migration belongs to (owner decision, 1 November 2026).
    ///
    /// <para>It carries no <c>BuildTargetModel</c>, for the same reason as
    /// <c>20261030000000_AddAttachmentStorageQuota</c>: <c>AppDbContextModelSnapshot</c> is the
    /// authoritative target model, and this migration changes data in two rows and no model at
    /// all.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261101090000_SetWeeklySupplierPriceFreshness")]
    public partial class SetWeeklySupplierPriceFreshness
    {
    }
}
