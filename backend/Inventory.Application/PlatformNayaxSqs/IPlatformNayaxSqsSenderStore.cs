using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformNayaxSqs;

/// <summary>
/// The write transaction a Nayax SQS sender decision runs in (issue #1101), the same shape issue
/// #982's plan-grant one is: on SQLite a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is
/// taken before the read the decision is made on. That is what makes the revision comparison inside
/// it meaningful - two administrators cannot both read revision 4 and both write revision 5 - and
/// disposing it without <see cref="CommitAsync"/> discards the sender row <em>and</em> its audit row
/// together.
/// </summary>
public interface IPlatformNayaxSqsSenderWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One business's recorded Nayax SQS sender, as this surface reads it (issue #1101). These five
/// values plus the two instants are the whole of the projection the business-pinned read is allowed
/// to make (AGENTS.md § Tenant ownership and data isolation).
/// </summary>
/// <param name="AwsSenderId">
/// The AWS IAM user id SQS reports as a message's <c>SenderId</c>, exactly as stored, or
/// <see langword="null"/> when none is recorded - a row that never had one, or one a removal
/// cleared.
/// </param>
/// <param name="IsEnabled">Whether the mapping is in force.</param>
/// <param name="LastMessageReceivedAtUtc">
/// When a message from this sender was last accepted, or <see langword="null"/> for one that has
/// never sent anything. Written by the queue reader of issue #1103 and only reported here.
/// </param>
/// <param name="Revision">
/// How many times this surface has written the row. It is the value a write has to present, and the
/// whole of the optimistic concurrency check: anything else is refused rather than overwriting
/// somebody else's decision.
/// </param>
/// <param name="CreatedAtUtc">When the row was created.</param>
/// <param name="UpdatedAtUtc">When it was last written.</param>
public sealed record PlatformNayaxSqsSenderState(
    string? AwsSenderId,
    bool IsEnabled,
    DateTime? LastMessageReceivedAtUtc,
    int Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// The business a Nayax SQS sender decision is about, as the store read it (issue #1101).
///
/// <para>The business id here is the key of the <em>stored business row</em>, which is what makes
/// the route value a subject rather than a tenancy scope: an id that matches no row produces no
/// target at all, and every write is addressed to the row this read found (AGENTS.md § Tenant
/// ownership and data isolation).</para>
///
/// <para>It carries the business's lifecycle status and deliberately nothing else about the
/// business - no name, contact address, ABN, member or Nayax credential value. The status is here
/// because a sender identity decides which business the shared queue's sales are saved into, and
/// only a trading business may have that answer changed.</para>
/// </summary>
/// <param name="BusinessId">The key of the business row that was found.</param>
/// <param name="Status">Its lifecycle status, read inside the decision's own transaction.</param>
/// <param name="Sender">
/// Its recorded sender, or <see langword="null"/> when no row has ever existed for it.
/// </param>
public sealed record PlatformNayaxSqsSenderTarget(
    int BusinessId,
    BusinessStatus Status,
    PlatformNayaxSqsSenderState? Sender);

/// <summary>
/// One committed Nayax SQS sender decision, as the store is asked to write it (issue #1101): the
/// row's new state, and the audit row that records how it got there. Both are written in one
/// <c>SaveChanges</c> inside the caller's transaction, so a database never holds one without the
/// other.
/// </summary>
/// <param name="BusinessId">
/// The business being recorded, taken from the row the write transaction read - never from the route
/// value directly.
/// </param>
/// <param name="AwsSenderId">
/// The sender id the row now carries, or <see langword="null"/> when the decision removed it.
/// </param>
/// <param name="IsEnabled">Whether the mapping is now in force.</param>
/// <param name="Revision">The revision this write produces: the stored one plus one, and <c>1</c> for a first row.</param>
/// <param name="FromAwsSenderId">The sender id the row carried before, for the audit row.</param>
/// <param name="FromIsEnabled">Whether the mapping was in force before, for the audit row.</param>
/// <param name="Actor">The platform administrator who decided it.</param>
/// <param name="OccurredAtUtc">
/// When the decision was made, sampled inside the write transaction from the application clock.
/// </param>
public sealed record PlatformNayaxSqsSenderWrite(
    int BusinessId,
    string? AwsSenderId,
    bool IsEnabled,
    int Revision,
    string? FromAwsSenderId,
    bool FromIsEnabled,
    ActorIdentity Actor,
    DateTime OccurredAtUtc);

/// <summary>
/// The narrow persistence port over one business's <strong>Nayax SQS sender identity</strong>, as
/// the Super Admin records it (issue #1101, child of #1100, under the owner's approvals of 5 and
/// 6 November 2026).
///
/// <para><strong>It is deliberately its own port.</strong> <c>IPlatformBusinessDirectoryStore</c>
/// reaches a business's lifecycle status and its memberships and - with one reviewed two-column
/// exception - no tenant-owned row at all, and it stays that way: a sender identity is tenant-owned,
/// so widening that store to write one would make the directory the thing that writes a business's
/// own data. What is here instead is three methods over exactly two tables -
/// <c>BusinessNayaxSqsSenders</c> and <c>BusinessNayaxSqsSenderAudits</c> - plus the business row the
/// route named, and nothing else is readable or writable through it. No method takes a predicate, a
/// column list, a page, a sort, or a sender id the caller may look <em>up</em> by.</para>
///
/// <para><strong>There is no lookup by sender id here, on purpose.</strong> "Which business does
/// this sender belong to?" is the queue reader's question and issue #1103 adds that narrow lookup
/// itself, as its own reviewed path. A method on this port that took a sender id would be a
/// cross-business read reachable from the platform-admin surface, which is a different decision from
/// the one this issue made.</para>
///
/// <para><strong>The business is a parameter here, and that is the exception's whole shape.</strong>
/// Every tenant-owned write in this application is addressed to the business the caller's own
/// membership resolved; a platform decision has to name the business it is about, so the id travels
/// in - checked against the stored business row, inside the write transaction, before anything is
/// written. It resolves no tenancy for the request, which still carries a <em>denied</em>
/// <c>BusinessScope</c>, and the adapter behind this port is the only one permitted to read or write
/// these two tables under one (see <c>EfPlatformNayaxSqsSenderStore</c>).</para>
/// </summary>
public interface IPlatformNayaxSqsSenderStore
{
    /// <summary>
    /// The business, its lifecycle status and its recorded sender, or <see langword="null"/> when no
    /// business row has that key.
    ///
    /// <para>Called twice by a decision: once before the transaction, to answer "no such business"
    /// without taking a write lock on the caller's behalf, and once <em>inside</em> it, which is the
    /// read every rule is actually decided on.</para>
    /// </summary>
    Task<PlatformNayaxSqsSenderTarget?> FindAsync(int businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the write transaction a decision runs in. The caller's write is saved inside it, and a
    /// refusal rolls back the sender row and its audit row together.
    /// </summary>
    Task<IPlatformNayaxSqsSenderWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the sender row and its audit row, in one <c>SaveChanges</c> inside the caller's
    /// transaction. It creates the row when the business holds none and updates the one it holds
    /// otherwise; it never deletes either row, and never touches any other table.
    /// </summary>
    /// <exception cref="Inventory.Domain.Exceptions.DomainConflictException">
    /// The sender id is already recorded for another business, as the global unique index reported
    /// it. Nothing is written, and the refusal names no other business.
    /// </exception>
    Task SaveAsync(PlatformNayaxSqsSenderWrite write, CancellationToken cancellationToken);
}

/// <summary>
/// One audit event about one committed Nayax SQS sender decision (issue #1101): who decided it,
/// which business it was about, whether a sender id was recorded before and after, whether the
/// mapping is in force, the revision the write produced, and when it happened.
///
/// <para><strong>There is no field for the sender id itself, deliberately.</strong> Both sides of it
/// are recorded in the database, in the same transaction as the change, where they are the evidence
/// of whose sales may be attributed to whom; this entry is what reaches the host's log pipeline, and
/// a sink that printed the whole entry still could not emit an AWS identifier. It carries no business
/// name, no member, no email address and no Entra identity but the actor's own pair - and no AWS
/// access key, secret or ARN, because this application stores none.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="BusinessId">The business the decision was about.</param>
/// <param name="HadSenderId">Whether a sender id was recorded before this decision.</param>
/// <param name="HasSenderId">Whether one is recorded now.</param>
/// <param name="WasEnabled">Whether the mapping was in force before.</param>
/// <param name="IsEnabled">Whether it is in force now.</param>
/// <param name="Revision">The revision this write produced.</param>
/// <param name="OccurredAtUtc">When it was committed, from the application clock.</param>
public sealed record PlatformNayaxSqsSenderAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    int BusinessId,
    bool HadSenderId,
    bool HasSenderId,
    bool WasEnabled,
    bool IsEnabled,
    int Revision,
    DateTime OccurredAtUtc);

/// <summary>
/// The log sink for Nayax SQS sender decisions, implemented at the InventoryApi boundary over
/// <c>ILogger</c> (issue #1101), for the same reasons as the plan-grant audit beside it: the event
/// carries the request's correlation id, which is a transport fact.
///
/// <para>It is <strong>beside</strong> the audit row this decision writes in its own transaction,
/// not instead of it. The row is the committed-together evidence the issue requires; this is the
/// operational trail every other platform-admin action already emits, and it is emitted only after
/// the commit.</para>
/// </summary>
public interface IPlatformNayaxSqsSenderAudit
{
    void Record(PlatformNayaxSqsSenderAuditEntry entry);
}
