using Inventory.Application.PlatformAdmin;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessSupplierProvisioningStore"/> (issue #849, child
/// C of #835). Like every other adapter in this folder it lives beside the
/// <see cref="AppDbContext"/> and the persistence models it reads and writes.
///
/// <para><strong>Three tables, in one direction.</strong> It reads the business row being approved,
/// reads the enabled presets once, and inserts one <see cref="Supplier"/> per preset. Nothing is
/// read back out of the business - no existing supplier is examined, compared or changed - and
/// nothing is written to <c>SupplierPresets</c>: the copy goes one way, and what lands in the
/// business is an ordinary tenant-owned row that business owns outright from that moment on.</para>
///
/// <para><strong>Why it may write at all, and how narrowly.</strong> The request it serves is the
/// platform administrator's, who is a member of no business, so it carries a <em>denied</em>
/// <c>BusinessScope</c> and <c>BusinessOwnershipEnforcer</c> refuses every tenant-owned write. This
/// adapter takes <c>AppDbContext.BeginSupplierProvisioning</c> around its single
/// <c>SaveChanges</c>, which permits exactly one thing: new <see cref="Supplier"/> rows stamped with
/// the business being approved. It is the only caller of that lease, the lease closes before the
/// method returns, and the business id is the key of the row the approval's own write transaction
/// read. There is no <c>IgnoreQueryFilters</c> here - in particular none over <c>Suppliers</c>, which
/// is why this path cannot see another business's suppliers even while it is permitted to create
/// this one's - no raw SQL and no unscoped context.</para>
///
/// <para><strong>The marker and the suppliers are one save.</strong> The copy instant goes on the
/// business row in the same <c>SaveChanges</c> as the supplier inserts, inside the caller's
/// <c>BEGIN IMMEDIATE</c> transaction, so there is no moment at which a database holds one without
/// the other: either the approval, the copies and the marker all commit, or none of them do.</para>
/// </summary>
public sealed class EfBusinessSupplierProvisioningStore : IBusinessSupplierProvisioningStore
{
    private readonly AppDbContext _db;

    public EfBusinessSupplierProvisioningStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task CopyEnabledSupplierPresetsAsync(
        int businessId,
        DateTime copiedAtUtc,
        CancellationToken cancellationToken)
    {
        // Tracked, because the marker below is written on this very row - and in the approval's own
        // transaction this is the same instance the status change was made on, so the two cannot
        // disagree about the business being approved.
        var business = await _db.Businesses
            .SingleOrDefaultAsync(candidate => candidate.Id == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (business is null || business.SupplierPresetsCopiedAtUtc is not null)
        {
            // No such business - unreachable from the approval, which has just read and written this
            // row - or the copy has already been made. Either way this writes nothing at all: the
            // marker is the one thing that decides, and it has already decided.
            return;
        }

        // One read, one snapshot: the presets as they are at this instant. A preset enabled,
        // disabled, renamed or given a website after this read is a change to the platform's
        // suggestions and never a change to what this business received.
        var presets = await _db.SupplierPresets
            .AsNoTracking()
            .Where(preset => preset.IsEnabled)
            // By the comparison key and then the row key, the same total order the Super Admin's own
            // list uses, so the rows are inserted in a deterministic order rather than the
            // database's.
            .OrderBy(preset => preset.NormalisedName)
            .ThenBy(preset => preset.Id)
            .Select(preset => new { preset.Name, preset.Url })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var preset in presets)
        {
            _db.Suppliers.Add(new Supplier
            {
                // The name and the website, and deliberately nothing else. The contact fields stay
                // null because the platform knows no contact for a supplier, and the three GST
                // defaults keep their own default of GstRules.None because a GST default is explicit
                // configuration a business makes about a supplier it buys from - never something
                // inferred from a name, a website or the fact that the row was suggested
                // (AGENTS.md § Product GST rules and supplier defaults).
                Name = preset.Name,
                Url = preset.Url,

                // BusinessId is deliberately left unset: the lease below is what stamps it, through
                // the same central enforcer every ordinary insert goes through, so this adapter
                // never assigns an owner itself.
            });
        }

        // Written whether or not there were any presets to copy. "The copy was made and there was
        // nothing enabled to copy" is a completed provisioning, and leaving the marker null would
        // turn the next approval attempt on this business into a second copy.
        business.SupplierPresetsCopiedAtUtc = copiedAtUtc;

        using (_db.BeginSupplierProvisioning(businessId))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
