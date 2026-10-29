using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Raised when a write would cross a business boundary. It is a fault, not a validation message:
/// reaching it means a caller tried to create, change, delete, or relate a record outside its own
/// business, so the whole <c>SaveChanges</c> is abandoned.
///
/// The message names the entity type and the rule that stopped it, never the other business's
/// data, so it is safe to log and safe to surface as a generic 403.
/// </summary>
public sealed class CrossBusinessAccessException : Exception
{
    public CrossBusinessAccessException(string message) : base(message)
    {
    }

    public CrossBusinessAccessException()
        : this("The requested write crosses a business ownership boundary.")
    {
    }

    public CrossBusinessAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The write-side half of tenant scoping (issue #64). Global query filters protect reads; this
/// protects everything else, centrally, on the way into the database.
///
/// It enforces four rules over the change tracker:
/// <list type="number">
///   <item><b>Stamping.</b> A new tenant-owned row gets the caller's business. Callers never
///   supply it, so no route, query, form, or JSON value can choose an owner.</item>
///   <item><b>Ownership.</b> An insert, update, or delete of a row belonging to another business
///   is rejected. Query filters already hide those rows from reads, but a detached entity
///   attached by id would otherwise slip straight through to an UPDATE.</item>
///   <item><b>Immutability.</b> The business key cannot be changed once set, so a record cannot
///   be moved between businesses.</item>
///   <item><b>Relationships.</b> Every foreign key that points at another tenant-owned entity
///   must point inside the same business - this is what stops a supplier, product, purchase, or
///   site agreement from one business being attached to another one's record.</item>
/// </list>
///
/// Rule 4 is driven by EF model metadata rather than a hand-written list of relationships, so a
/// new foreign key between tenant-owned entities is covered the day it is mapped.
///
/// Since issue #551 it enforces a fifth, earlier rule: a request running in a support session
/// (<see cref="BusinessScopeState.SupportReadOnly"/>) may write <em>nothing at all</em>. That is
/// what makes "read-only" a property of this boundary rather than of each endpoint - the HTTP
/// method rule in the capability authorization handler already refuses every non-<c>GET</c> call in
/// a session, and this is what catches a <c>GET</c> that turns out to have a side effect. It covers
/// every entity rather than only the tenant-owned ones, because in this state there is no write
/// this application wants: not a tenant-owned row, not a membership, not an invite, not a business.
///
/// Since issue #849 the denied-scope rule has one reviewed exception, and it is narrower than the
/// rule rather than a way around it: while <c>AppDbContext.BeginSupplierProvisioning</c> holds a
/// lease, a denied scope may create <em>new <see cref="Supplier"/> rows of that one business</em> -
/// the copies a newly approved business gets of the platform supplier presets that serve it - and
/// nothing else. See <see cref="EnforceSupplierProvisioning"/>.
///
/// Since issue #982 there is a second, on the same terms and reviewed the same way: while
/// <c>AppDbContext.BeginPlanGrantWrite</c> holds a lease, a denied scope may create or change
/// <em>one business's <see cref="BusinessPlanGrant"/> row</em> and append the
/// <see cref="BusinessPlanGrantAudit"/> row beside it - the Super Admin's manual plan grant - and
/// nothing else. See <see cref="EnforcePlanGrantWrite"/>.
/// </summary>
internal static class BusinessOwnershipEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        var scope = context.BusinessScope;

        if (scope.State == BusinessScopeState.SupportReadOnly)
        {
            // Refused before the stamping and ownership rules below are even considered: those
            // decide which business a write belongs to, and the answer here is that there is no
            // such write. A SaveChanges with nothing tracked is not a write and is left alone, so a
            // read path that happens to call it is not turned into a failure.
            if (HasPendingWrite(context))
            {
                throw new SupportSessionReadOnlyException();
            }

            return;
        }

        if (scope.State == BusinessScopeState.Unscoped)
        {
            // The legacy direct-construction path: no scope to enforce against. Relationship
            // consistency is still checked below so pre-tenant code cannot build a graph that
            // would be illegal once it runs scoped.
            EnforceRelationships(context, currentBusinessId: null);
            return;
        }

        if (scope.BusinessId is not { } businessId)
        {
            // Denied scope. Reads already return nothing; writes must not be the way around it -
            // with one reviewed exception, below, which is narrower than this branch rather than a
            // way out of it.
            if (context.SupplierProvisioningBusinessId is { } provisionedBusinessId)
            {
                EnforceSupplierProvisioning(context, provisionedBusinessId);
                return;
            }

            if (context.PlanGrantWriteBusinessId is { } grantedBusinessId)
            {
                EnforcePlanGrantWrite(context, grantedBusinessId);
                return;
            }

            if (context.ChangeTracker.Entries<IBusinessOwned>().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                throw new CrossBusinessAccessException(
                    "A tenant-owned write was attempted without a resolved current business. "
                        + "The caller has no business membership, so the write is refused.");
            }

            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<IBusinessOwned>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampOrVerifyNewRow(entry, businessId);
                    break;

                case EntityState.Modified:
                    VerifyUnchangedOwnership(entry, businessId);
                    break;

                case EntityState.Deleted:
                    VerifyOwnership(entry, businessId, "deleted");
                    break;

                default:
                    break;
            }
        }

        EnforceRelationships(context, businessId);
    }

    /// <summary>
    /// The provisioning window of <c>AppDbContext.BeginSupplierProvisioning</c> (issue #849): on a
    /// denied scope, <em>new <see cref="Supplier"/> rows of <paramref name="businessId"/></em> are
    /// stamped and accepted, and every other tenant-owned write is refused exactly as it is without
    /// a lease.
    ///
    /// <para>It is the only exception to "a denied scope writes nothing tenant-owned", and it is
    /// deliberately smaller than that rule in three directions at once: one entity type, one state
    /// (an insert - never an update and never a delete), and one business, whose id the approval's
    /// own write transaction read rather than a request supplying it. Stamping goes through the same
    /// <see cref="StampOrVerifyNewRow"/> every ordinary insert uses, so a row that arrives already
    /// carrying some <em>other</em> business's key is refused here too rather than silently
    /// re-owned, and <see cref="EnforceRelationships"/> still runs, so a copied supplier could not
    /// be related to another business's row.</para>
    /// </summary>
    private static void EnforceSupplierProvisioning(AppDbContext context, int businessId)
    {
        foreach (var entry in context.ChangeTracker.Entries<IBusinessOwned>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity is Supplier:
                    StampOrVerifyNewRow(entry, businessId);
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot be written while provisioning a "
                            + "business: the provisioning path may only create that business's own "
                            + "Supplier rows.");

                default:
                    break;
            }
        }

        EnforceRelationships(context, businessId);
    }

    /// <summary>
    /// The manual plan grant window of <c>AppDbContext.BeginPlanGrantWrite</c> (issue #982): on a
    /// denied scope, <em>one business's <see cref="BusinessPlanGrant"/> row and the
    /// <see cref="BusinessPlanGrantAudit"/> row beside it</em> may be written, and every other
    /// tenant-owned write is refused exactly as it is without a lease.
    ///
    /// <para>It is the second exception to "a denied scope writes nothing tenant-owned", reviewed on
    /// its own issue, and it is deliberately smaller than that rule in three directions at once: two
    /// entity types, two states (an insert, and an update of the grant row - never a delete, because
    /// a grant that was given is never unsaid), and one business, whose id the decision's own write
    /// transaction read rather than a request supplying it. The audit row may only be inserted: an
    /// audit trail that could be updated is not one.</para>
    ///
    /// <para>Stamping and ownership go through the same <see cref="StampOrVerifyNewRow"/> and
    /// <see cref="VerifyUnchangedOwnership"/> every ordinary write uses, so a row carrying some
    /// <em>other</em> business's key is refused here too rather than silently re-owned, and
    /// <see cref="EnforceRelationships"/> still runs.</para>
    /// </summary>
    private static void EnforcePlanGrantWrite(AppDbContext context, int businessId)
    {
        foreach (var entry in context.ChangeTracker.Entries<IBusinessOwned>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity is BusinessPlanGrant or BusinessPlanGrantAudit:
                    StampOrVerifyNewRow(entry, businessId);
                    break;

                case EntityState.Modified when entry.Entity is BusinessPlanGrant:
                    VerifyUnchangedOwnership(entry, businessId);
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot be written while deciding a business's "
                            + "plan grant: that path may only create or change that business's own "
                            + "plan grant and append its audit row.");

                default:
                    break;
            }
        }

        EnforceRelationships(context, businessId);
    }

    /// <summary>
    /// Whether this <c>SaveChanges</c> would write anything at all - any entity type, any of the
    /// three write states. Deliberately not limited to <see cref="IBusinessOwned"/>: the support
    /// session's rule is about writing, not about ownership.
    /// </summary>
    private static bool HasPendingWrite(AppDbContext context) =>
        context.ChangeTracker
            .Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private static void StampOrVerifyNewRow(EntityEntry<IBusinessOwned> entry, int businessId)
    {
        if (entry.Entity.BusinessId == 0)
        {
            entry.Entity.BusinessId = businessId;
            return;
        }

        VerifyOwnership(entry, businessId, "created");
    }

    /// <summary>
    /// An update may not change the business key. Checking the original value as well as the
    /// current one is what makes this a move check rather than only an ownership check: loading
    /// your own row, reassigning its business, and saving would otherwise hand the record away.
    /// </summary>
    private static void VerifyUnchangedOwnership(EntityEntry<IBusinessOwned> entry, int businessId)
    {
        var property = entry.Property(nameof(IBusinessOwned.BusinessId));
        var original = property.OriginalValue as int? ?? 0;
        var current = entry.Entity.BusinessId;

        if (original != current)
        {
            throw new CrossBusinessAccessException(
                $"{entry.Metadata.ClrType.Name} ownership is immutable: a record cannot be moved "
                    + "to another business.");
        }

        VerifyOwnership(entry, businessId, "updated");
    }

    private static void VerifyOwnership(EntityEntry<IBusinessOwned> entry, int businessId, string operation)
    {
        if (entry.Entity.BusinessId != businessId)
        {
            throw new CrossBusinessAccessException(
                $"{entry.Metadata.ClrType.Name} belongs to another business and cannot be {operation} "
                    + "by the current caller.");
        }
    }

    /// <summary>
    /// Rejects a foreign key that reaches across businesses.
    ///
    /// The principal's business is taken from the change tracker when it is already loaded or
    /// being inserted in this same save, and otherwise read from the database with
    /// <c>IgnoreQueryFilters</c>. Ignoring the filter is the point: a principal in another
    /// business is invisible to a scoped read, and "invisible" must be reported as a boundary
    /// violation rather than mistaken for "does not exist".
    /// </summary>
    private static void EnforceRelationships(AppDbContext context, int? currentBusinessId)
    {
        var entries = context.ChangeTracker
            .Entries<IBusinessOwned>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var entry in entries)
        {
            var dependentBusinessId = entry.Entity.BusinessId;

            foreach (var foreignKey in entry.Metadata.GetForeignKeys())
            {
                var principalType = foreignKey.PrincipalEntityType.ClrType;

                // Only relationships between tenant-owned entities can cross a boundary. The
                // Business foreign key itself is the ownership column and is handled above.
                if (!typeof(IBusinessOwned).IsAssignableFrom(principalType))
                {
                    continue;
                }

                var keyValues = foreignKey.Properties
                    .Select(property => entry.Property(property.Name).CurrentValue)
                    .ToArray();

                if (keyValues.Any(value => value is null))
                {
                    // An optional relationship that is not set cannot cross anything.
                    continue;
                }

                var principalBusinessId = FindPrincipalBusinessId(context, principalType, foreignKey, keyValues);

                if (principalBusinessId is null)
                {
                    // No such principal at all. That is a referential-integrity problem, not an
                    // ownership one, so leave it to the foreign key constraint to report.
                    continue;
                }

                if (principalBusinessId != dependentBusinessId)
                {
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot reference "
                            + $"{principalType.Name} from another business.");
                }
            }
        }
    }

    private static int? FindPrincipalBusinessId(
        AppDbContext context,
        Type principalType,
        Microsoft.EntityFrameworkCore.Metadata.IForeignKey foreignKey,
        object?[] keyValues)
    {
        var principalKeyNames = foreignKey.PrincipalKey.Properties.Select(p => p.Name).ToArray();

        var tracked = context.ChangeTracker
            .Entries<IBusinessOwned>()
            .FirstOrDefault(candidate =>
                principalType.IsInstanceOfType(candidate.Entity)
                && candidate.State != EntityState.Deleted
                && principalKeyNames
                    .Select(name => candidate.Property(name).CurrentValue)
                    .SequenceEqual(keyValues));

        if (tracked is not null)
        {
            return tracked.Entity.BusinessId;
        }

        var query = (IQueryable<IBusinessOwned>)context
            .GetType()
            .GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(principalType)
            .Invoke(context, null)!;

        return query
            .IgnoreQueryFilters()
            .Where(BuildKeyPredicate(principalType, principalKeyNames, keyValues))
            .Select(entity => (int?)entity.BusinessId)
            .FirstOrDefault();
    }

    private static System.Linq.Expressions.Expression<Func<IBusinessOwned, bool>> BuildKeyPredicate(
        Type principalType,
        string[] keyNames,
        object?[] keyValues)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(IBusinessOwned), "e");
        var typed = System.Linq.Expressions.Expression.Convert(parameter, principalType);

        System.Linq.Expressions.Expression? predicate = null;

        for (var i = 0; i < keyNames.Length; i++)
        {
            var property = System.Linq.Expressions.Expression.Property(typed, keyNames[i]);
            var value = System.Linq.Expressions.Expression.Constant(keyValues[i], property.Type);
            var comparison = System.Linq.Expressions.Expression.Equal(property, value);

            predicate = predicate is null
                ? comparison
                : System.Linq.Expressions.Expression.AndAlso(predicate, comparison);
        }

        return System.Linq.Expressions.Expression.Lambda<Func<IBusinessOwned, bool>>(predicate!, parameter);
    }
}
