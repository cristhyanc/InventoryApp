using Inventory.Application.Legal;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILegalAcceptanceStore"/> (issue #1259). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model
/// it writes.
///
/// <para><strong>One statement, and it is an insert.</strong> There is no read, no update and no
/// delete here and no method to add one through: nothing in the application reads an acceptance back
/// - the evidence is read by a human from the database, and the Super Admin view of it is explicitly
/// out of scope for this issue - and an agreement that could be edited or removed would not be
/// evidence. The <c>SaveChanges</c> runs inside the caller's own <c>BEGIN IMMEDIATE</c> transaction,
/// so the row commits with the application or the redemption it belongs to or not at all.</para>
///
/// <para><strong>The write window is the whole of the tenancy story.</strong> Both request paths
/// that reach here carry a <em>denied</em> <c>BusinessScope</c> - somebody applying for a business
/// has no membership and somebody joining one does not have it yet - and the row is tenant-owned, so
/// <c>BusinessOwnershipEnforcer</c> would refuse it. <see cref="AppDbContext.BeginLegalAcceptanceWrite"/>
/// is the narrow, reviewed exception: one entity type, inserts only, one business, whose id this
/// adapter receives from the use case's own write transaction and never from a request. The business
/// key is <em>not</em> set on the entity here - the enforcer stamps it from the lease, exactly as it
/// stamps an ordinary tenant-owned insert - so there is no code path in which this adapter chooses an
/// owner.</para>
/// </summary>
public sealed class EfLegalAcceptanceStore : ILegalAcceptanceStore
{
    private readonly AppDbContext _db;

    public EfLegalAcceptanceStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task RecordAsync(LegalAcceptance acceptance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);

        _db.BusinessLegalAcceptances.Add(new Models.BusinessLegalAcceptance
        {
            // Deliberately no BusinessId: the lease below is what tells the enforcer which business
            // this row belongs to, and the enforcer is what writes the key.
            DirectoryTenantId = acceptance.Identity.DirectoryTenantId,
            ObjectId = acceptance.Identity.ObjectId,
            Version = acceptance.Version,
            AcceptedAtUtc = acceptance.AcceptedAtUtc,
            Context = acceptance.Context,
        });

        // One save, one lease: the window is open for exactly this statement and closes before this
        // method returns, so nothing else on this context can be written through it.
        using (_db.BeginLegalAcceptanceWrite(acceptance.BusinessId.Value))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
