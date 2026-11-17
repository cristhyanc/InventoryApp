using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

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
/// Since issue #849 the denied-scope rule has reviewed exceptions, and each is narrower than the
/// rule rather than a way around it. While <c>AppDbContext.BeginSupplierProvisioning</c> holds a
/// lease, a denied scope may create <em>new <see cref="Supplier"/> rows of that one business</em> -
/// the copies a newly approved business gets of the platform supplier presets that serve it - and
/// nothing else (see <see cref="EnforceSupplierProvisioning"/>). Since issue #941, while
/// <c>AppDbContext.BeginPriceCatalogueMatching</c> holds one, it may <em>update the four match
/// columns</em> of the price-tracking mappings an automatic match applies to - and nothing else (see
/// <see cref="EnforcePriceCatalogueMatching"/>).
///
/// Since issue #982 there is a third, on the same terms and reviewed the same way: while
/// <c>AppDbContext.BeginPlanGrantWrite</c> holds a lease, a denied scope may create or change
/// <em>one business's <see cref="BusinessPlanGrant"/> row</em> and append the
/// <see cref="BusinessPlanGrantAudit"/> row beside it - the Super Admin's manual plan grant - and
/// nothing else. See <see cref="EnforcePlanGrantWrite"/>.
///
/// Since issue #1101 there is a fifth, on exactly the third's terms: while
/// <c>AppDbContext.BeginNayaxSqsSenderWrite</c> holds a lease, a denied scope may create or change
/// <em>one business's <see cref="BusinessNayaxSqsSender"/> row</em> and append the
/// <see cref="BusinessNayaxSqsSenderAudit"/> row beside it - which AWS IAM user sends that
/// business's Nayax sales - and nothing else, a delete of either included. See
/// <see cref="EnforceNayaxSqsSenderWrite"/>.
///
/// Since issue #1282 the cost of all of it grows with the rows rather than with their square, and
/// the rules themselves are untouched: the tracked graph is read <em>once</em> per save and handed to
/// every rule below, the principal of a foreign key is found in an index built once per principal
/// type rather than by scanning the change tracker again for every key, and principals that are only
/// in the database are asked about in one statement per principal type (in fixed-size batches of
/// ids) rather than one statement per foreign key. See <see cref="EnforceRelationships"/>.
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

        // Read once, enforced many times (issue #1282). ChangeTracker.Entries<T>() runs change
        // detection on every call, so asking it again for every rule - and, as the relationship
        // rule used to, for every foreign key - is most of what made one save of N new rows cost
        // N^2. Stamping below changes a property of an already-tracked entity, which neither adds
        // nor removes an entry, so this one list stays the whole truth for the rest of this save.
        var entries = context.ChangeTracker.Entries<IBusinessOwned>().ToList();

        if (scope.State == BusinessScopeState.Unscoped)
        {
            // The legacy direct-construction path: no scope to enforce against. Relationship
            // consistency is still checked below so pre-tenant code cannot build a graph that
            // would be illegal once it runs scoped.
            EnforceRelationships(context, entries);
            return;
        }

        if (scope.BusinessId is not { } businessId)
        {
            EnforceDeniedScope(context, entries);
            return;
        }

        foreach (var entry in entries)
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

        EnforceRelationships(context, entries);
    }

    /// <summary>
    /// The denied-scope branch of <see cref="Enforce"/>: a request that resolved no business writes
    /// nothing tenant-owned, except through one of the five reviewed leases.
    /// </summary>
    private static void EnforceDeniedScope(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        // Denied scope. Reads already return nothing; writes must not be the way around it -
        // with five reviewed exceptions, below, each narrower than this branch rather than a
        // way out of it.
        if (context.SupplierProvisioningBusinessId is { } provisionedBusinessId)
        {
            EnforceSupplierProvisioning(context, entries, provisionedBusinessId);
            return;
        }

        if (context.PriceCatalogueMatchingLeased)
        {
            EnforcePriceCatalogueMatching(context, entries);
            return;
        }

        if (context.PriceCatalogueReviewLeased)
        {
            EnforcePriceCatalogueReview(context, entries);
            return;
        }

        if (context.PlanGrantWriteBusinessId is { } grantedBusinessId)
        {
            EnforcePlanGrantWrite(context, entries, grantedBusinessId);
            return;
        }

        if (context.NayaxSqsSenderWriteBusinessId is { } senderBusinessId)
        {
            EnforceNayaxSqsSenderWrite(context, entries, senderBusinessId);
            return;
        }

        if (entries.Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            throw new CrossBusinessAccessException(
                "A tenant-owned write was attempted without a resolved current business. "
                    + "The caller has no business membership, so the write is refused.");
        }
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
    private static void EnforceSupplierProvisioning(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries,
        int businessId)
    {
        foreach (var entry in entries)
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

        EnforceRelationships(context, entries);
    }

    /// <summary>
    /// The columns of a <see cref="ProductPriceTracking"/> row a match may write - the collector's
    /// automatic one (issue #941) and the Super Admin's own, including an undo (issue #928): where the
    /// mapping has got to, which master product it is, how that was decided, and when.
    ///
    /// <para>Written out rather than derived, and asserted as a <em>set</em> rather than as a lower
    /// bound: the business key, the product, the waiting key and the captured search text are all
    /// outside it, so the matching window cannot move a mapping between businesses, re-point it at
    /// another product, or change what the collector searches for.</para>
    /// </summary>
    private static readonly string[] MatchableTrackingColumns =
    [
        nameof(ProductPriceTracking.Status),
        nameof(ProductPriceTracking.MasterProductId),
        nameof(ProductPriceTracking.MatchSource),
        nameof(ProductPriceTracking.UpdatedAtUtc),
    ];

    /// <summary>
    /// The matching window of <c>AppDbContext.BeginPriceCatalogueMatching</c> (issue #941): on a
    /// denied scope, <em>updates to <see cref="MatchableTrackingColumns"/> of
    /// <see cref="ProductPriceTracking"/> rows</em> are accepted, and every other tenant-owned write
    /// is refused exactly as it is without a lease.
    ///
    /// <para>It is the second exception to "a denied scope writes nothing tenant-owned", and like the
    /// first it is deliberately smaller than the rule in several directions at once: one entity type,
    /// one state (an update - never an insert and never a delete, so the collector can neither start
    /// nor withdraw a business's request to be priced), and four columns, which do not include the
    /// ownership key. Unlike the provisioning window it names no business, because a match is decided
    /// for a waiting key and the mappings that carry that key may belong to any number of businesses -
    /// so what bounds it is what may be written rather than whose row it is, and
    /// <see cref="EnforceRelationships"/> still runs, so no mapping can be left pointing at another
    /// business's product.</para>
    /// </summary>
    private static void EnforcePriceCatalogueMatching(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Modified when entry.Entity is ProductPriceTracking:
                    VerifyMatchColumnsOnly(entry);
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot be written while matching a tracked "
                            + "product: the matching path may only update the match columns of a "
                            + "price-tracking mapping.");

                default:
                    break;
            }
        }

        EnforceRelationships(context, entries);
    }

    /// <summary>
    /// The review window of <c>AppDbContext.BeginPriceCatalogueReview</c> (issue #928): on a denied
    /// scope, <em>updates to <see cref="MatchableTrackingColumns"/> of
    /// <see cref="ProductPriceTracking"/> rows</em> are accepted, and every other tenant-owned write
    /// is refused exactly as it is without a lease.
    ///
    /// <para>It is the fourth exception to "a denied scope writes nothing tenant-owned" and it is the
    /// second one's rule for the other decider: a Super Admin's match, and an undo of an automatic
    /// one, are decided for a waiting <em>key</em>, and the mappings that carry one key may belong to
    /// any number of businesses. So it names no business either, what bounds it is the same written-out
    /// column set - which does not include the ownership key, the product, the waiting key or the
    /// captured search text - and it is the same one state, an update: this surface can no more create
    /// or withdraw a business's request to be priced than the matcher can.
    /// <see cref="EnforceRelationships"/> still runs, so no mapping is left pointing at another
    /// business's product.</para>
    /// </summary>
    private static void EnforcePriceCatalogueReview(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Modified when entry.Entity is ProductPriceTracking:
                    VerifyMatchColumnsOnly(entry);
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot be written while reviewing the price "
                            + "catalogue: the review path may only update the match columns of a "
                            + "price-tracking mapping.");

                default:
                    break;
            }
        }

        EnforceRelationships(context, entries);
    }

    /// <summary>
    /// Refuses an update that would change anything but <see cref="MatchableTrackingColumns"/>. The
    /// message names the columns and never the row's business.
    /// </summary>
    private static void VerifyMatchColumnsOnly(EntityEntry<IBusinessOwned> entry)
    {
        var unexpected = entry.Properties
            .Where(property => property.IsModified)
            .Select(property => property.Metadata.Name)
            .Except(MatchableTrackingColumns, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unexpected.Count > 0)
        {
            throw new CrossBusinessAccessException(
                "A price-tracking mapping may only have its match columns written while a match is "
                    + $"being applied or taken back: {string.Join(", ", unexpected)} cannot be "
                    + "changed this way.");
        }
    }

    /// <summary>
    /// The manual plan grant window of <c>AppDbContext.BeginPlanGrantWrite</c> (issue #982): on a
    /// denied scope, <em>one business's <see cref="BusinessPlanGrant"/> row and the
    /// <see cref="BusinessPlanGrantAudit"/> row beside it</em> may be written, and every other
    /// tenant-owned write is refused exactly as it is without a lease.
    ///
    /// <para>It is the third exception to "a denied scope writes nothing tenant-owned", reviewed on
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
    private static void EnforcePlanGrantWrite(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries,
        int businessId)
    {
        foreach (var entry in entries)
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

        EnforceRelationships(context, entries);
    }

    /// <summary>
    /// The Nayax SQS sender window of <c>AppDbContext.BeginNayaxSqsSenderWrite</c> (issue #1101): on
    /// a denied scope, <em>one business's <see cref="BusinessNayaxSqsSender"/> row and the
    /// <see cref="BusinessNayaxSqsSenderAudit"/> row beside it</em> may be written, and every other
    /// tenant-owned write is refused exactly as it is without a lease.
    ///
    /// <para>It is the fifth exception to "a denied scope writes nothing tenant-owned", reviewed on
    /// its own issue, and it is the third one's shape exactly: two entity types, two states (an
    /// insert, and an update of the sender row - <strong>never a delete of either</strong>, because
    /// removing a sender is an update that clears its id and an audit trail that could be removed is
    /// not one), and one business, whose id the decision's own write transaction read rather than a
    /// request supplying it. The audit row may only be inserted: an audit trail that could be updated
    /// is not one.</para>
    ///
    /// <para>Stamping and ownership go through the same <see cref="StampOrVerifyNewRow"/> and
    /// <see cref="VerifyUnchangedOwnership"/> every ordinary write uses, so a row carrying some
    /// <em>other</em> business's key is refused here too rather than silently re-owned, and
    /// <see cref="EnforceRelationships"/> still runs.</para>
    /// </summary>
    private static void EnforceNayaxSqsSenderWrite(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries,
        int businessId)
    {
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added
                    when entry.Entity is BusinessNayaxSqsSender or BusinessNayaxSqsSenderAudit:
                    StampOrVerifyNewRow(entry, businessId);
                    break;

                case EntityState.Modified when entry.Entity is BusinessNayaxSqsSender:
                    VerifyUnchangedOwnership(entry, businessId);
                    break;

                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    throw new CrossBusinessAccessException(
                        $"{entry.Metadata.ClrType.Name} cannot be written while recording a "
                            + "business's Nayax SQS sender: that path may only create or change that "
                            + "business's own sender row and append its audit row, and may delete "
                            + "neither.");

                default:
                    break;
            }
        }

        EnforceRelationships(context, entries);
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
    /// <para>The rule is the one issue #64 wrote: every foreign key of every added or modified
    /// tenant-owned row that points at another tenant-owned entity must point inside the row's own
    /// business, and a principal that does not exist at all is left to the foreign key constraint,
    /// because that is a referential-integrity problem rather than an ownership one.</para>
    ///
    /// <para>What issue #1282 changed is only how the principals are found. The old shape ran one
    /// change-detection pass and one full scan of the change tracker <em>per foreign key</em>, and
    /// made one database query per untracked principal, so one save of 4,000 new rows took 26
    /// seconds and held SQLite's single write lock for all of it. Now:</para>
    /// <list type="number">
    ///   <item>the tracked graph is read once by <see cref="Enforce"/> and indexed once per
    ///   principal type and key by <see cref="TrackedPrincipalIndex"/>;</item>
    ///   <item>the principals that are only in the database are asked about in one statement per
    ///   principal type and dependent business - in fixed-size batches of ids - and what they are
    ///   asked is the narrow question this rule actually has: does any stored row under these keys
    ///   belong to a <em>different</em> business? See <see cref="UntrackedPrincipals"/>;</item>
    ///   <item>only when the answer is yes does the check resolve one key at a time, so a refusal
    ///   still names the first offending row in change-tracker order with the message it has always
    ///   had. That path is a fault and the whole save is abandoned, so its cost buys correctness in
    ///   the only case that pays it.</item>
    /// </list>
    ///
    /// <para>Both lookups deliberately use <c>IgnoreQueryFilters</c>: a principal in another
    /// business is invisible to a scoped read, and "invisible" must be reported as a boundary
    /// violation rather than mistaken for "does not exist".</para>
    /// </summary>
    private static void EnforceRelationships(
        AppDbContext context,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        var checks = CollectRelationshipChecks(entries);

        if (checks.Count == 0)
        {
            return;
        }

        foreach (var principals in GroupPrincipalsThisSaveDoesNotHold(checks, entries))
        {
            principals.Resolve(context);
        }

        JudgeRelationshipChecks(context, checks);
    }

    /// <summary>
    /// Answers every check the tracked graph can answer, and groups the rest by principal type,
    /// principal key and dependent business - one group is one set of statements, whatever number of
    /// foreign keys landed in it.
    /// </summary>
    private static IEnumerable<UntrackedPrincipals> GroupPrincipalsThisSaveDoesNotHold(
        IReadOnlyList<RelationshipCheck> checks,
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        var tracked = new TrackedPrincipalIndex(entries);
        var untracked = new Dictionary<(Type PrincipalType, IKey PrincipalKey, int DependentBusinessId), UntrackedPrincipals>();

        foreach (var check in checks)
        {
            check.TrackedPrincipalBusinessId =
                tracked.BusinessIdOf(check.PrincipalType, check.PrincipalKey, check.KeyValues);

            if (check.TrackedPrincipalBusinessId is not null)
            {
                continue;
            }

            var group = (check.PrincipalType, check.PrincipalKey, check.DependentBusinessId);

            if (!untracked.TryGetValue(group, out var principals))
            {
                principals = new UntrackedPrincipals(
                    check.PrincipalType, check.PrincipalKey, check.DependentBusinessId);
                untracked.Add(group, principals);
            }

            principals.Add(check.KeyValues);
            check.UntrackedPrincipals = principals;
        }

        return untracked.Values;
    }

    /// <summary>
    /// The comparison itself, over the checks in change-tracker order - which is the order the
    /// key-by-key check judged them in, so the first refusal is the same row with the same message.
    /// </summary>
    private static void JudgeRelationshipChecks(
        AppDbContext context,
        IReadOnlyList<RelationshipCheck> checks)
    {
        foreach (var check in checks)
        {
            int? principalBusinessId;

            if (check.TrackedPrincipalBusinessId is { } trackedBusinessId)
            {
                principalBusinessId = trackedBusinessId;
            }
            else if (!check.UntrackedPrincipals!.HoldsAPrincipalOfAnotherBusiness)
            {
                // The statements this group made proved that no stored row under any of its keys
                // belongs to another business, so this foreign key either points inside the
                // dependent's own business or points at nothing at all - and neither is a
                // violation.
                continue;
            }
            else
            {
                principalBusinessId = check.UntrackedPrincipals.OwnerOf(context, check.KeyValues);
            }

            if (principalBusinessId is null)
            {
                // No such principal at all. That is a referential-integrity problem, not an
                // ownership one, so leave it to the foreign key constraint to report.
                continue;
            }

            if (principalBusinessId != check.DependentBusinessId)
            {
                throw new CrossBusinessAccessException(
                    $"{check.Entry.Metadata.ClrType.Name} cannot reference "
                        + $"{check.PrincipalType.Name} from another business.");
            }
        }
    }

    /// <summary>
    /// Every foreign key this save has to judge, in change-tracker order, with the key values read
    /// once. Collecting them before resolving anything is what lets the principals be loaded in
    /// bulk; judging them in this same order afterwards is what keeps the refusal identical to the
    /// key-by-key check it replaced.
    /// </summary>
    private static List<RelationshipCheck> CollectRelationshipChecks(
        IReadOnlyList<EntityEntry<IBusinessOwned>> entries)
    {
        var checks = new List<RelationshipCheck>();

        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

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

                if (TryReadKeyValues(entry, foreignKey.Properties, out var keyValues))
                {
                    checks.Add(new RelationshipCheck(
                        entry, principalType, foreignKey.PrincipalKey, keyValues, dependentBusinessId));
                }
            }
        }

        return checks;
    }

    /// <summary>
    /// The current values of one key of one entry, in the key's own order, or <c>false</c> when any
    /// of them is not set: an optional relationship that is not set cannot cross anything, and a key
    /// value that is not set matches no foreign key, because every value this rule compares is
    /// non-null.
    /// </summary>
    private static bool TryReadKeyValues(
        EntityEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        out object?[] keyValues)
    {
        keyValues = new object?[keyProperties.Count];

        for (var i = 0; i < keyValues.Length; i++)
        {
            keyValues[i] = entry.Property(keyProperties[i].Name).CurrentValue;

            if (keyValues[i] is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One foreign key of one added or modified row, and everything the ownership comparison needs
    /// about it.
    /// </summary>
    private sealed class RelationshipCheck
    {
        public RelationshipCheck(
            EntityEntry<IBusinessOwned> entry,
            Type principalType,
            IKey principalKey,
            object?[] keyValues,
            int dependentBusinessId)
        {
            Entry = entry;
            PrincipalType = principalType;
            PrincipalKey = principalKey;
            KeyValues = keyValues;
            DependentBusinessId = dependentBusinessId;
        }

        public EntityEntry<IBusinessOwned> Entry { get; }

        public Type PrincipalType { get; }

        public IKey PrincipalKey { get; }

        public object?[] KeyValues { get; }

        /// <summary>
        /// The business the dependent row itself belongs to - read before the comparison, exactly as
        /// the per-key check read it, and after stamping has run.
        /// </summary>
        public int DependentBusinessId { get; }

        /// <summary>The principal's business when this save holds it, and null otherwise.</summary>
        public int? TrackedPrincipalBusinessId { get; set; }

        /// <summary>The group this key was asked about in, when this save does not hold it.</summary>
        public UntrackedPrincipals? UntrackedPrincipals { get; set; }
    }

    /// <summary>
    /// The principals this save already holds, indexed by principal type, principal key and key
    /// values rather than scanned for.
    ///
    /// <para>One index is built per principal type and key, on first use, from the one list of
    /// entries <see cref="Enforce"/> read - so a save of N rows with one foreign key each walks the
    /// tracked graph once instead of N times. The first entry wins for a given key, which is the one
    /// a scan would have found first, and a <see cref="EntityState.Deleted"/> entry is skipped, as
    /// it was before: a row being deleted is not a principal anything may point at.</para>
    /// </summary>
    private sealed class TrackedPrincipalIndex
    {
        private readonly IReadOnlyList<EntityEntry<IBusinessOwned>> _entries;
        private readonly Dictionary<(Type PrincipalType, IKey PrincipalKey), Dictionary<object?[], int>> _indexes = [];

        public TrackedPrincipalIndex(IReadOnlyList<EntityEntry<IBusinessOwned>> entries) => _entries = entries;

        public int? BusinessIdOf(Type principalType, IKey principalKey, object?[] keyValues) =>
            IndexFor(principalType, principalKey).TryGetValue(keyValues, out var businessId)
                ? businessId
                : null;

        private Dictionary<object?[], int> IndexFor(Type principalType, IKey principalKey)
        {
            if (_indexes.TryGetValue((principalType, principalKey), out var existing))
            {
                return existing;
            }

            var index = new Dictionary<object?[], int>(KeyValuesComparer.Instance);

            foreach (var candidate in _entries)
            {
                if (candidate.State == EntityState.Deleted || !principalType.IsInstanceOfType(candidate.Entity))
                {
                    continue;
                }

                if (TryReadKeyValues(candidate, principalKey.Properties, out var keyValues))
                {
                    index.TryAdd(keyValues, candidate.Entity.BusinessId);
                }
            }

            _indexes.Add((principalType, principalKey), index);
            return index;
        }
    }

    /// <summary>
    /// The principals of one principal type, one principal key and one dependent business that this
    /// save does <em>not</em> hold, and the one question the relationship rule has about them: does
    /// any stored row under these keys belong to a different business?
    ///
    /// <para>Asking that instead of "whose is each of these rows?" is what makes the answer one
    /// statement rather than one per key. A "no" clears every key in the group at once: each of them
    /// then points either inside the dependent's own business or at no row at all, and neither is a
    /// violation. A "yes" is a fault, and <see cref="EnforceRelationships"/> falls back to the
    /// key-by-key resolution to name the offending row.</para>
    ///
    /// <para>The ids are asked about in fixed-size batches so neither the statement nor its
    /// parameters grow with the size of a save; a batch is one statement, so a save needs
    /// <c>ceil(distinct ids / batch size)</c> of them per group.</para>
    /// </summary>
    private sealed class UntrackedPrincipals
    {
        /// <summary>
        /// Ids per statement for a single-column principal key, which is nearly every one of them.
        /// EF Core sends them as one parameter, so this bounds the parameter rather than the SQL.
        /// </summary>
        private const int SingleColumnKeyBatchSize = 1_000;

        /// <summary>
        /// Key tuples per statement for a composite principal key, where each tuple is its own
        /// bracketed comparison in the SQL rather than one value in a list. Deliberately small:
        /// SQLite's default expression-depth limit is 1,000 nested terms.
        /// </summary>
        private const int CompositeKeyBatchSize = 100;

        private readonly Type _principalType;
        private readonly string[] _keyNames;
        private readonly int _dependentBusinessId;
        private readonly HashSet<object?[]> _keyValues = new(KeyValuesComparer.Instance);
        private Dictionary<object?[], int?>? _owners;

        public UntrackedPrincipals(Type principalType, IKey principalKey, int dependentBusinessId)
        {
            _principalType = principalType;
            _keyNames = KeyNamesOf(principalKey);
            _dependentBusinessId = dependentBusinessId;
        }

        /// <summary>
        /// Whether some stored row under one of these keys belongs to another business. False until
        /// <see cref="Resolve"/> has run, and false afterwards means every key in the group is
        /// cleared.
        /// </summary>
        public bool HoldsAPrincipalOfAnotherBusiness { get; private set; }

        public void Add(object?[] keyValues) => _keyValues.Add(keyValues);

        public void Resolve(AppDbContext context)
        {
            var batchSize = _keyNames.Length == 1 ? SingleColumnKeyBatchSize : CompositeKeyBatchSize;

            foreach (var batch in _keyValues.Chunk(batchSize))
            {
                var foreign = PrincipalQuery(context, _principalType)
                    .IgnoreQueryFilters()
                    .Any(OfAnotherBusinessPredicate(batch));

                if (foreign)
                {
                    HoldsAPrincipalOfAnotherBusiness = true;
                    return;
                }
            }
        }

        /// <summary>
        /// The exact owner of one of these keys, on the fault path only - once
        /// <see cref="HoldsAPrincipalOfAnotherBusiness"/> is true, the refusal has to name a row.
        /// Memoised by key, because a batch write repeats one principal across many rows and a
        /// refused save should not cost one statement per row to refuse.
        /// </summary>
        public int? OwnerOf(AppDbContext context, object?[] keyValues)
        {
            _owners ??= new Dictionary<object?[], int?>(KeyValuesComparer.Instance);

            if (_owners.TryGetValue(keyValues, out var businessId))
            {
                return businessId;
            }

            businessId = ReadPrincipalBusinessId(context, keyValues);
            _owners.Add(keyValues, businessId);
            return businessId;
        }

        /// <summary>
        /// The exact owner of one principal, read key by key. Unchanged from the per-key lookup this
        /// rule used before issue #1282, and reached only when a group's own statement has already
        /// shown that some row under one of its keys belongs to another business - so a refusal names
        /// the same row, with the same message, as it always did.
        /// </summary>
        private int? ReadPrincipalBusinessId(AppDbContext context, object?[] keyValues)
        {
            var parameter = Expression.Parameter(typeof(IBusinessOwned), "e");
            var typed = Expression.Convert(parameter, _principalType);
            var predicate = Expression.Lambda<Func<IBusinessOwned, bool>>(
                KeyValuesPredicate(typed, _keyNames, keyValues), parameter);

            return PrincipalQuery(context, _principalType)
                .IgnoreQueryFilters()
                .Where(predicate)
                .Select(entity => (int?)entity.BusinessId)
                .FirstOrDefault();
        }

        private Expression<Func<IBusinessOwned, bool>> OfAnotherBusinessPredicate(object?[][] batch)
        {
            var parameter = Expression.Parameter(typeof(IBusinessOwned), "e");
            var typed = Expression.Convert(parameter, _principalType);

            var underOneOfTheKeys = _keyNames.Length == 1
                ? OneOfPredicate(typed, _keyNames[0], batch)
                : AnyKeyValuesPredicate(typed, _keyNames, batch);

            var ofAnotherBusiness = Expression.NotEqual(
                Expression.Property(typed, nameof(IBusinessOwned.BusinessId)),
                Expression.Constant(_dependentBusinessId));

            return Expression.Lambda<Func<IBusinessOwned, bool>>(
                Expression.AndAlso(underOneOfTheKeys, ofAnotherBusiness), parameter);
        }

        /// <summary>
        /// <c>key IN (...)</c> for a single-column principal key. The ids are reached through a
        /// property of a holder rather than written in as a constant list, which is the shape of an
        /// ordinary closure: EF Core parameterises it, so one batch of any size is one statement and
        /// the compiled query is reused across saves instead of recompiled for every set of ids.
        /// </summary>
        private static Expression OneOfPredicate(Expression typed, string keyName, object?[][] keyValues)
        {
            var property = Expression.Property(typed, keyName);
            var listType = typeof(List<>).MakeGenericType(property.Type);
            var values = (System.Collections.IList)Activator.CreateInstance(listType, keyValues.Length)!;

            foreach (var keyValue in keyValues)
            {
                values.Add(keyValue[0]);
            }

            var holder = Activator.CreateInstance(
                typeof(KeyValueBatch<>).MakeGenericType(property.Type), values)!;
            var held = Expression.Property(Expression.Constant(holder), nameof(KeyValueBatch<object>.Values));

            return Expression.Call(held, listType.GetMethod(nameof(List<object>.Contains), [property.Type])!, property);
        }

        /// <summary>Any one of several key tuples, for a composite principal key.</summary>
        private static Expression AnyKeyValuesPredicate(Expression typed, string[] keyNames, object?[][] keyValues)
        {
            Expression? anyOfThem = null;

            foreach (var keyValue in keyValues)
            {
                var one = KeyValuesPredicate(typed, keyNames, keyValue);
                anyOfThem = anyOfThem is null ? one : Expression.OrElse(anyOfThem, one);
            }

            return anyOfThem!;
        }

        /// <summary>One key tuple, compared column by column.</summary>
        private static Expression KeyValuesPredicate(Expression typed, string[] keyNames, object?[] keyValues)
        {
            Expression? predicate = null;

            for (var i = 0; i < keyNames.Length; i++)
            {
                var property = Expression.Property(typed, keyNames[i]);
                var value = Expression.Constant(keyValues[i], property.Type);
                var comparison = Expression.Equal(property, value);

                predicate = predicate is null ? comparison : Expression.AndAlso(predicate, comparison);
            }

            return predicate!;
        }

        private static string[] KeyNamesOf(IKey key) =>
            key.Properties.Select(property => property.Name).ToArray();

        private static readonly MethodInfo SetMethod =
            typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

        /// <summary>
        /// <c>DbContext.Set&lt;T&gt;()</c> per principal type, resolved reflectively once for the
        /// life of the process rather than on every lookup.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, MethodInfo> TypedSetMethods = new();

        private static IQueryable<IBusinessOwned> PrincipalQuery(AppDbContext context, Type principalType) =>
            (IQueryable<IBusinessOwned>)TypedSetMethods
                .GetOrAdd(principalType, static type => SetMethod.MakeGenericMethod(type))
                .Invoke(context, null)!;
    }

    /// <summary>
    /// Key values compared the way the per-key scan compared them: element by element, with the
    /// default equality of the boxed values, which is what <c>SequenceEqual</c> used. Hashing uses
    /// the same equality, so two tuples that compare equal always land in the same bucket.
    /// </summary>
    private sealed class KeyValuesComparer : IEqualityComparer<object?[]>
    {
        public static readonly KeyValuesComparer Instance = new();

        public bool Equals(object?[]? left, object?[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!EqualityComparer<object?>.Default.Equals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] keyValues)
        {
            var hash = new HashCode();

            foreach (var keyValue in keyValues)
            {
                hash.Add(keyValue);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// One batch of ids, held behind a property so that EF Core treats them as a parameter rather
    /// than as constants baked into the SQL. See <see cref="UntrackedPrincipals.OneOfPredicate"/>.
    /// </summary>
    private sealed class KeyValueBatch<T>
    {
        public KeyValueBatch(List<T> values) => Values = values;

        public List<T> Values { get; }
    }
}
