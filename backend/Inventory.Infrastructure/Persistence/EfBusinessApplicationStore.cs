using Inventory.Application.Onboarding;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessApplicationStore"/> (issue #541). Like every
/// other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence
/// models it reads and writes.
///
/// <para><strong>It is the whole of what an applicant can reach, in one file.</strong> Two tables -
/// businesses and memberships - and two statements. The request it serves carries a <em>denied</em>
/// <c>BusinessScope</c>, because nothing resolved a business for a person who is asking to create
/// one, and both tables are structurally non-tenant-owned
/// (docs/architecture.md § Tenant ownership), so no query filter hides a row from it and
/// <c>BusinessOwnershipEnforcer</c> stamps nothing for it. That makes the business id in these
/// statements the entire boundary: the read's id came from the applicant's own membership row, and
/// the write's is the key the database assigns to the row it creates. Neither ever came from the
/// request. There is no <c>IgnoreQueryFilters</c>, no raw SQL and no unscoped context
/// anywhere.</para>
///
/// <para><strong>Nothing here reads or writes business data.</strong> No product, purchase, sale or
/// document is reachable through this adapter: those are all tenant-owned, the request's scope is
/// denied, and the filters are therefore fully in force over everything else on the context.</para>
///
/// <para><strong>The write goes through <c>SaveChanges</c> on tracked entities, deliberately.</strong>
/// The central enforcers that validate a business row - <see cref="BusinessStatusEnforcer"/>,
/// <see cref="BusinessTimeZoneEnforcer"/> and <see cref="BusinessAbnEnforcer"/> - run there, and a
/// set-based insert would write straight past all three. It runs inside the caller's
/// <c>BEGIN IMMEDIATE</c> transaction and saves before returning, because
/// <c>MembershipWriteGuard</c> re-reads the identity's rows afterwards to judge what this write left
/// behind; an unsaved change would leave it judging the old state, and this is also where the
/// filtered one-active-membership index can refuse the insert.</para>
/// </summary>
public sealed class EfBusinessApplicationStore : IBusinessApplicationStore
{
    private readonly AppDbContext _db;

    public EfBusinessApplicationStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<BusinessApplication?> FindAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        var row = await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => new
            {
                business.Id,
                business.Name,
                business.ContactEmail,
                business.Abn,
                business.GstRegistered,
                business.TimeZoneId,
                // The stored status exactly as it is, including a value this code does not declare:
                // the caller has to see it to refuse it rather than treat it as a pending
                // application.
                business.Status,
                business.AppliedAtUtc,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new BusinessApplication(
                BusinessId.From(row.Id),
                new BusinessSettings(row.Name, row.ContactEmail, row.Abn, row.GstRegistered, row.TimeZoneId),
                row.Status,
                row.AppliedAtUtc);
    }

    /// <inheritdoc />
    public async Task<BusinessApplication> CreateAsync(
        BusinessSettings settings,
        ActorIdentity identity,
        MemberProfile profile,
        DateTime appliedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(profile);

        var business = new Models.Business
        {
            Name = settings.Name,
            ContactEmail = settings.ContactEmail,
            Abn = settings.Abn,
            GstRegistered = settings.GstRegistered,
            TimeZoneId = settings.TimeZoneId,
            // Stated, never defaulted (issue #584): an application is waiting for a decision, so it
            // is Pending and no endpoint serves its data.
            Status = BusinessStatus.Pending,
            AppliedAtUtc = appliedAtUtc,
            // Nobody has decided anything yet, so neither column is written. Leaving them null is
            // the honest record of that; a decision instant copied from the application instant
            // would claim a business event that never happened.
            DecidedAtUtc = null,
            RejectionReason = null,
            CreatedAtUtc = appliedAtUtc,
        };

        // Added through the business's own navigation so one SaveChanges inserts both rows with the
        // foreign key fixed up by EF: the membership's owner is the row being created in the same
        // operation, and there is no moment at which a Pending business exists with no Owner.
        business.Memberships.Add(new Models.BusinessMembership
        {
            DirectoryTenantId = identity.DirectoryTenantId,
            ObjectId = identity.ObjectId,
            // Display-only, and absent stays absent rather than becoming a placeholder (issue
            // #524). Neither column is read by any access decision.
            DisplayName = profile.DisplayName,
            Email = profile.Email,
            // The applicant owns what they applied for. It is the only role an application can
            // create: a business with no Owner would have nobody who could ever invite anybody.
            Role = BusinessRole.Owner,
            IsActive = true,
            CreatedAtUtc = appliedAtUtc,
            // One instant for the creation and the state change alike, so the row never describes a
            // state it reached at some other time (issue #522).
            StatusChangedAtUtc = appliedAtUtc,
        });

        _db.Businesses.Add(business);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new BusinessApplication(
            BusinessId.From(business.Id),
            settings,
            business.Status,
            business.AppliedAtUtc);
    }
}
