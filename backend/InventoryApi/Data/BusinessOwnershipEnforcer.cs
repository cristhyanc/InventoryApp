using Inventory.Application.Tenancy;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace InventoryApi.Data;

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
/// </summary>
internal static class BusinessOwnershipEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        var scope = context.BusinessScope;

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
            // Denied scope. Reads already return nothing; writes must not be the way around it.
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
