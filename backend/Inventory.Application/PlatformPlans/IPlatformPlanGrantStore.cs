using Inventory.Application.Tenancy;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformPlans;

/// <summary>
/// Which kind of decision one manual-grant write was (issue #982). It is stated rather than inferred
/// from the plans on either side: a change that grants the same plan with a different end date moved
/// nothing, and ending a grant leaves its plan exactly as it was.
/// </summary>
public enum PlatformPlanGrantChange
{
    /// <summary>The business held no manual grant and now holds one.</summary>
    Created = 1,

    /// <summary>It held one, and its plan, its end date or both were changed.</summary>
    Changed = 2,

    /// <summary>Its grant was ended, so it falls back to whatever else entitles it.</summary>
    Ended = 3,
}

/// <summary>
/// The write transaction a manual-grant decision runs in (issue #982), the same shape
/// <see cref="IBusinessMembershipWriteStore"/>'s is: on SQLite a <c>BEGIN IMMEDIATE</c> transaction,
/// so the write lock is taken before the read the decision is made on. That is what makes the
/// revision comparison inside it meaningful - two administrators cannot both read revision 4 and
/// both write revision 5 - and disposing it without <see cref="CommitAsync"/> discards the grant
/// <em>and</em> its audit row together.
/// </summary>
public interface IPlatformPlanGrantWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One manual grant as stored, as this surface reads it (issue #982).
/// </summary>
/// <param name="Plan">The granted plan, exactly as stored - an undeclared value included.</param>
/// <param name="Period">When it applies, half-open and in UTC (issue #966).</param>
/// <param name="Reason">Why it was granted, as the administrator typed it.</param>
/// <param name="Revision">
/// How many times this surface has written the grant. It is the value a write has to present, and
/// the whole of the optimistic concurrency check: a stale one is refused rather than overwriting
/// somebody else's decision.
/// </param>
/// <param name="GrantedAtUtc">When the grant was last written, from the application clock.</param>
public sealed record PlatformPlanGrantState(
    SubscriptionPlan Plan,
    EntitlementPeriod Period,
    string? Reason,
    int Revision,
    DateTime GrantedAtUtc);

/// <summary>
/// The business a manual-grant decision is about, as the store read it (issue #982).
///
/// <para>The business id here is the key of the <em>stored business row</em>, which is what makes
/// the route value a subject rather than a tenancy scope: an id that matches no row produces no
/// target at all, and every write is addressed to the row this read found (AGENTS.md § Tenant
/// ownership and data isolation).</para>
/// </summary>
/// <param name="BusinessId">The key of the business row that was found.</param>
/// <param name="Name">Its display name, so an answer needs no second read.</param>
/// <param name="Grants">
/// Every <em>complimentary</em> grant the business holds - never the grandfathered one, which this
/// surface does not read, change or end. It is a list rather than one row because the schema of
/// issue #966 permits several, and the Application layer refuses to decide between two rather than
/// an adapter silently choosing one.
/// </param>
public sealed record PlatformPlanGrantTarget(
    int BusinessId,
    string Name,
    IReadOnlyList<PlatformPlanGrantState> Grants);

/// <summary>
/// One committed manual-grant decision, as the store is asked to write it (issue #982): the grant
/// row's new state, and the audit row that records how it got there. Both are written in one
/// <c>SaveChanges</c> inside the caller's transaction, so a database never holds one without the
/// other.
/// </summary>
/// <param name="BusinessId">
/// The business being granted, taken from the row the write transaction read - never from the route
/// value directly.
/// </param>
/// <param name="Plan">The plan the grant now carries.</param>
/// <param name="StartsAtUtc">The instant it applies from, inclusive.</param>
/// <param name="EndsAtUtc">The instant it stops at, exclusive, or <see langword="null"/> for none.</param>
/// <param name="GrantReason">
/// The reason the grant row carries: the administrator's new one when a grant is created or
/// changed, and the reason it was granted for when it is being ended - because the row records why
/// the business has the plan, not why it stopped having it.
/// </param>
/// <param name="AuditReason">
/// The reason the audit row carries: the one the administrator typed for <em>this</em> decision,
/// including an end. Both are stored in the database, and both are deliberately absent from
/// <see cref="PlatformPlanGrantAuditEntry"/>, so no log can carry either.
/// </param>
/// <param name="Revision">The revision this write produces: the stored one plus one.</param>
/// <param name="Change">Which kind of decision it was.</param>
/// <param name="FromPlan">
/// The plan the grant carried before, or <see langword="null"/> when the business held none.
/// </param>
/// <param name="Actor">The platform administrator who decided it.</param>
/// <param name="OccurredAtUtc">
/// When the decision was made, sampled inside the write transaction from the application clock.
/// </param>
public sealed record PlatformPlanGrantWrite(
    int BusinessId,
    SubscriptionPlan Plan,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    string GrantReason,
    string AuditReason,
    int Revision,
    PlatformPlanGrantChange Change,
    SubscriptionPlan? FromPlan,
    ActorIdentity Actor,
    DateTime OccurredAtUtc);

/// <summary>
/// The narrow persistence port over one business's <strong>manual plan grant</strong>, as the Super
/// Admin decides it (issue #982, the owner-approved amendment of 29 October 2026).
///
/// <para><strong>It is deliberately its own port.</strong> <c>IPlatformBusinessDirectoryStore</c>
/// reaches a business's lifecycle status and its memberships and no tenant-owned row at all, and it
/// stays that way: a grant is a tenant-owned record of issue #966, so widening that store to write
/// one would make the directory the thing that writes a business's own data. What is here instead is
/// four methods over exactly two tables - <c>BusinessPlanGrants</c> and
/// <c>BusinessPlanGrantAudits</c> - and nothing else is readable or writable through it. No method
/// takes a predicate, a column list, a page, a sort or a plan the caller may apply to several
/// businesses at once.</para>
///
/// <para><strong>The billing subscription is not on this port at all.</strong> A manual grant and a
/// paid subscription are two records and neither writes the other (#965, decided 29 October 2026):
/// this surface cannot read the subscription to validate a grant and cannot change it, and
/// <see cref="EffectiveEntitlementPolicy"/> remains the one rule that reads both.</para>
///
/// <para><strong>The business is a parameter here, and that is the exception's whole shape.</strong>
/// Every tenant-owned write in this application is addressed to the business the caller's own
/// membership resolved; a platform decision has to name the business it is about, so the id travels
/// in - checked against the stored business row, inside the write transaction, before anything is
/// written. It resolves no tenancy for the request, which still carries a <em>denied</em>
/// <c>BusinessScope</c>, and the adapter behind this port is the only one permitted to write these
/// two tables under one (see <c>EfPlatformPlanGrantStore</c>).</para>
/// </summary>
public interface IPlatformPlanGrantStore
{
    /// <summary>
    /// The business and its complimentary grants, or <see langword="null"/> when no business row has
    /// that key.
    ///
    /// <para>Called twice by a decision: once before the transaction, to answer "no such business"
    /// without taking a write lock on the caller's behalf, and once <em>inside</em> it, which is the
    /// read every rule is actually decided on.</para>
    /// </summary>
    Task<PlatformPlanGrantTarget?> FindAsync(int businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the write transaction a decision runs in. The caller's write is saved inside it, and a
    /// refusal rolls back the grant and its audit row together.
    /// </summary>
    Task<IPlatformPlanGrantWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes the grant and its audit row, in one <c>SaveChanges</c> inside the caller's
    /// transaction. It creates the grant when the business holds none and updates the one it holds
    /// otherwise; it never deletes a grant, never writes the grandfathered one, and never touches
    /// any other table.
    /// </summary>
    Task SaveAsync(PlatformPlanGrantWrite write, CancellationToken cancellationToken);
}

/// <summary>
/// One audit event about one committed manual-grant decision (issue #982): who decided it, which
/// business it was about, which plan it moved from and to, when the grant now ends, the revision the
/// write produced, and when it happened.
///
/// <para><strong>There is no field for the reason, deliberately.</strong> The reason is recorded in
/// the database, in the same transaction as the grant, where it is the evidence of why a business
/// was given a plan; this entry is what reaches the host's log pipeline, and a sink that printed the
/// whole entry still could not emit the administrator's prose. It carries no business name, no
/// member, no email address and no Entra identity but the actor's own pair.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the decision was about.</param>
/// <param name="Change">Which kind of decision it was.</param>
/// <param name="FromPlan">
/// The plan the grant carried before, or <see langword="null"/> when the business held none.
/// </param>
/// <param name="ToPlan">
/// The plan it carries now, or <see langword="null"/> when the grant was ended - which entitles
/// nothing from this instant rather than granting Free.
/// </param>
/// <param name="EndsAtUtc">
/// When the grant now stops entitling, exclusive, or <see langword="null"/> for an open-ended one.
/// </param>
/// <param name="Revision">The revision this write produced.</param>
/// <param name="OccurredAtUtc">When it was committed, from the application clock.</param>
public sealed record PlatformPlanGrantAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    PlatformPlanGrantChange Change,
    SubscriptionPlan? FromPlan,
    SubscriptionPlan? ToPlan,
    DateTime? EndsAtUtc,
    int Revision,
    DateTime OccurredAtUtc);

/// <summary>
/// The log sink for manual-grant decisions, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #982), for the same reasons as the platform-business audits beside it: the
/// event carries the request's correlation id, which is a transport fact.
///
/// <para>It is <strong>beside</strong> the audit row this decision writes in its own transaction,
/// not instead of it. The row is the committed-together evidence the issue requires; this is the
/// operational trail every other platform-admin action already emits, and it is emitted only after
/// the commit.</para>
/// </summary>
public interface IPlatformPlanGrantAudit
{
    void Record(PlatformPlanGrantAuditEntry entry);
}
