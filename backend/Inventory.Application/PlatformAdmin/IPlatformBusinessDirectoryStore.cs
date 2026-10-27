using Inventory.Domain.Tenancy;

namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// One business as the Super Admin's directory reports it (issue #544): what it is called, where it
/// is in its lifecycle, the dates of its application and its decision, the contact detail its
/// application recorded, the state or territory it trades in (issue #933), how many people can
/// currently reach it, and whether it has stored Nayax credentials.
///
/// <para><strong>What it deliberately does not carry.</strong> No sale, stock, cost, purchase,
/// product, machine, site or report value - nothing tenant-owned at all - and no member's name,
/// email address or Entra identifier. The Super Admin decides whether a business may trade; they
/// are not thereby a reader of every business's books, and this record is the whole of what the
/// directory can say.</para>
///
/// <para><strong>It carries the business id, and that is the one place in this application where a
/// business key is a normal part of a response.</strong> Every tenant-facing DTO leaves it out
/// (AGENTS.md § Tenant ownership) because a member is told about their own business and never
/// handed the tenant key. Here the id is the subject of the decision rather than a scope: it names
/// which application is being approved or rejected, it is never stamped on a row, and it resolves
/// no tenancy - the request that carries it back still runs with a denied
/// <c>BusinessScope</c>.</para>
/// </summary>
/// <param name="BusinessId">The business's own key, which the approve and reject calls name.</param>
/// <param name="Name">The business's display name, exactly as stored.</param>
/// <param name="Status">The stored lifecycle status, exactly as stored (issue #540).</param>
/// <param name="CreatedAtUtc">When the business row was created.</param>
/// <param name="AppliedAtUtc">
/// When it applied, or <see langword="null"/> for a business that never went through an application
/// - the existing one, and anything <c>bootstrap-business</c> adopted (issue #540).
/// </param>
/// <param name="DecidedAtUtc">
/// When its application was approved or refused, or <see langword="null"/> while no decision has
/// been recorded.
/// </param>
/// <param name="ContactEmail">
/// The business's own contact address as its application recorded it, or <see langword="null"/>.
/// It is the business's published detail, not a member's personal address, and it is the one piece
/// of contact information the directory reports.
/// </param>
/// <param name="Abn">The stored 11-digit ABN, or <see langword="null"/> when none was recorded.</param>
/// <param name="State">
/// The Australian state or territory the business recorded, as one of
/// <see cref="AustralianStates.All"/>, or <see langword="null"/> for a business that has not
/// recorded one (issue #933). It is reported because reviewing an application is exactly when an
/// operator needs to see where the applicant trades; like the name and the ABN it is the business's
/// own published detail and nothing tenant-owned.
/// </param>
/// <param name="ActiveMemberCount">
/// How many of its memberships are currently active - that is, how many people can reach the
/// business at all. Revoked rows are deliberately not counted: the question the directory answers
/// is "who can reach this business now", and a rejected application reads <c>0</c> because the
/// applicant's membership was made inactive by the rejection.
/// </param>
/// <param name="NayaxConfigured">
/// Whether the business has stored Nayax credentials at all - the existence of its single
/// <c>BusinessNayaxConnection</c> row, which is what <c>NotConfigured</c> is the absence of. It is
/// a yes/no and nothing more: no operator id, no connection status, and under no circumstances any
/// part of the token.
/// </param>
public sealed record PlatformBusinessSummary(
    int BusinessId,
    string Name,
    BusinessStatus Status,
    DateTime CreatedAtUtc,
    DateTime? AppliedAtUtc,
    DateTime? DecidedAtUtc,
    string? ContactEmail,
    string? Abn,
    string? State,
    int ActiveMemberCount,
    bool NayaxConfigured);

/// <summary>
/// The applicant of one pending business (issue #544): the membership the approval confirms and the
/// Entra identity <see cref="MembershipEligibility"/> is re-checked for.
///
/// <para>A pending business has exactly one membership - the self-service application creates the
/// business and the applicant's own active Owner membership as one save (issue #541), and no invite
/// can be redeemed into a business that does not resolve - so "the applicant" is not a choice
/// between candidates. A business whose rows say otherwise has no identifiable applicant, and
/// <see cref="BusinessDecisionPolicy.ApplicantUnavailableMessage"/> refuses the decision rather than
/// picking one.</para>
/// </summary>
/// <param name="MembershipId">The membership's own key - what the eligibility exclusion names.</param>
/// <param name="Identity">
/// The applicant's Entra <c>(tid, oid)</c> pair, or <see langword="null"/> when the stored pair is
/// not a usable identity at all. Only a hand-written row can produce that, and it fails closed
/// rather than being approved without the eligibility check that pair is the subject of.
/// </param>
/// <param name="IsActive">The membership's own state, exactly as persistence read it.</param>
public sealed record PlatformBusinessApplicant(int MembershipId, ActorIdentity? Identity, bool IsActive);

/// <summary>
/// The facts one pending decision is measured against (issue #544), read authoritatively inside the
/// write transaction the decision is made in.
/// </summary>
/// <param name="BusinessId">The business the decision is about.</param>
/// <param name="Name">Its display name, so the decision can answer without a second read.</param>
/// <param name="Status">Its stored status, exactly as stored, including an undeclared value.</param>
/// <param name="Applicant">
/// Its applicant, or <see langword="null"/> when the business holds no single membership that could
/// be one.
/// </param>
public sealed record PlatformBusinessDecisionTarget(
    int BusinessId,
    string Name,
    BusinessStatus Status,
    PlatformBusinessApplicant? Applicant);

/// <summary>
/// A recorded decision, as the Super Admin is answered (issue #544).
/// </summary>
/// <param name="BusinessId">The business that was decided.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The status it now holds - <c>Active</c> or <c>Rejected</c>.</param>
/// <param name="DecidedAtUtc">The instant the decision was recorded at.</param>
/// <param name="RejectionReason">
/// The stored reason for a rejection, or <see langword="null"/> for an approval and for a rejection
/// recorded without one.
/// </param>
public sealed record PlatformBusinessDecision(
    int BusinessId,
    string Name,
    BusinessStatus Status,
    DateTime DecidedAtUtc,
    string? RejectionReason);

/// <summary>
/// The narrow persistence port the Super Admin's business directory, its two decisions on an
/// application and its three lifecycle transitions run on (issues #544 and #545, two slices of #509
/// under #328).
///
/// <para><strong>Why it is a port of its own, and why it is this small.</strong> It is the first
/// write-capable platform-admin path, and the first request path of any kind that reads across every
/// business through <c>AppDbContext</c>. What keeps that from being a general cross-business context
/// is that the whole surface is here, in one reviewable interface: a fixed set of business columns,
/// the count of active memberships, the existence of a Nayax connection row, the memberships of one
/// named business, and writes that change nothing but a business's lifecycle status, its decision
/// instant and rejection reason, and one of its memberships' activation. There is no method that
/// takes a table name, a predicate, a column list or a statement, and no method that reaches a
/// tenant-owned row - so there is nothing here that a later change could widen without changing this
/// file (AGENTS.md § Unrestricted access is an explicit opt-in).</para>
///
/// <para><strong>One operation reaches past this port, deliberately.</strong> Deactivating a
/// business must leave no usable invite behind, and cancelling every live invite of one business is
/// already <see cref="Members.IMemberInviteStore.CancelAllLiveAsync"/> - written for this caller
/// (issue #531) as a single conditional statement so it commits or rolls back with the deactivation
/// itself. <c>BusinessLifecycleWriter</c> calls it with the business id of the row it has just read
/// inside its own transaction, never the route value, and <c>MemberInvite</c> is one of the four
/// structural non-tenant-owned tables, so nothing about that reaches a tenant-owned row either. A
/// second implementation of the same cancellation here would be two statements that could drift
/// apart.</para>
///
/// <para><strong>Every write runs inside issue #522's shared write transaction</strong>, through the
/// request's own <c>AppDbContext</c>, so <c>MembershipWriteGuard</c>'s re-checks see what was
/// written and a refusal rolls the business and the membership back together. The read the decision
/// is judged from is <see cref="FindDecisionTargetAsync"/>, called inside that same transaction:
/// the authoritative read, the comparison and the mutation are one operation, which is what makes a
/// concurrent approve and reject resolve to one winner rather than two.</para>
///
/// <para><strong>No business id here ever stamps anything.</strong> The ids are the subject of a
/// platform decision, not a tenancy scope: the request carries a <em>denied</em>
/// <c>BusinessScope</c> throughout, so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force over everything that is tenant-owned.</para>
/// </summary>
public interface IPlatformBusinessDirectoryStore
{
    /// <summary>
    /// Every business, in a deterministic order the caller does not have to re-sort, with the
    /// directory fields of <see cref="PlatformBusinessSummary"/> and nothing else.
    ///
    /// <para>Pending applications come first, so the queue the Super Admin is here for is at the
    /// top, and businesses are ordered by their own key within a status group.</para>
    /// </summary>
    Task<IReadOnlyList<PlatformBusinessSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// How many businesses are waiting for a decision. Counted in the database rather than by
    /// listing and filtering, because it is the badge a page polls.
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The facts a decision on <paramref name="businessId"/> is measured against, or
    /// <see langword="null"/> when no such business row exists.
    ///
    /// <para>It must be called inside the decision's own write transaction: the status and the
    /// applicant's activation are exactly the mutable facts a concurrent decision changes.</para>
    /// </summary>
    Task<PlatformBusinessDecisionTarget?> FindDecisionTargetAsync(
        int businessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the approval: the business becomes <see cref="BusinessStatus.Active"/> and
    /// <paramref name="decidedAtUtc"/> is stored as its decision instant. No membership is touched -
    /// the applicant's is already active, which is precisely what the caller has just re-checked -
    /// and no rejection reason is written.
    ///
    /// <para>It persists its own change before returning, because <c>MembershipWriteGuard</c>
    /// re-reads afterwards to judge the state the write left behind.</para>
    /// </summary>
    Task ApproveAsync(int businessId, DateTime decidedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Records the rejection: the business becomes <see cref="BusinessStatus.Rejected"/> with
    /// <paramref name="reason"/> and <paramref name="decidedAtUtc"/>, and the applicant's membership
    /// named by <paramref name="deactivateMembershipId"/> is made inactive with the same instant as
    /// its state-change instant - so the refused applicant is free to apply again.
    /// </summary>
    /// <param name="businessId">The business being refused.</param>
    /// <param name="reason">The validated reason to store, or <see langword="null"/> for none.</param>
    /// <param name="decidedAtUtc">The decision instant, stamped on both rows this writes.</param>
    /// <param name="deactivateMembershipId">
    /// The applicant's membership, or <see langword="null"/> when it is already inactive and there
    /// is nothing to change. A row whose state did not change must not have its state-change instant
    /// rewritten: that would claim a transition that never happened (issue #522).
    /// </param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task RejectAsync(
        int businessId,
        string? reason,
        DateTime decidedAtUtc,
        int? deactivateMembershipId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The facts a lifecycle transition on <paramref name="businessId"/> is measured against -
    /// its status and every membership it holds - or <see langword="null"/> when no such business
    /// row exists (issue #545).
    ///
    /// <para>It must be called inside the transition's own write transaction: the status and the
    /// memberships are exactly the mutable facts a concurrent decision, release or revocation
    /// changes. It is a separate read from <see cref="FindDecisionTargetAsync"/> because the two
    /// answer different questions - a decision needs the single applicant and their Entra identity,
    /// a lifecycle transition needs every membership's role and state and no identity at all.</para>
    /// </summary>
    Task<PlatformBusinessLifecycleTarget?> FindLifecycleTargetAsync(
        int businessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the deactivation: the business becomes <see cref="BusinessStatus.Deactivated"/>
    /// (issue #545). Its status column is the only thing this writes.
    ///
    /// <para>No membership is touched - the members keep their active flag and are refused because
    /// the business no longer resolves - and no decision instant or rejection reason is written: a
    /// deactivation is not a decision on an application, and overwriting <c>DecidedAtUtc</c> would
    /// make the row claim its application was decided at the moment it was switched off.</para>
    ///
    /// <para>It persists its own change before returning, because the caller runs inside
    /// <c>MembershipWriteGuard</c>'s transaction and the statements that follow - the invite
    /// cancellation, and any hook - have to see what this wrote.</para>
    /// </summary>
    Task DeactivateAsync(int businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Records the reactivation: the business becomes <see cref="BusinessStatus.Active"/> again
    /// (issue #545), and nothing else changes.
    ///
    /// <para>It restores no membership and no invite. A membership that was released or revoked
    /// while the business was off stays inactive, and a cancelled invite stays cancelled: both are
    /// deliberate decisions about a person, and a business returning is not a reason to undo
    /// them.</para>
    /// </summary>
    Task ReactivateAsync(int businessId, CancellationToken cancellationToken);

    /// <summary>
    /// Releases one membership of one business: it becomes inactive, with
    /// <paramref name="releasedAtUtc"/> as its state-change instant, written in the same operation
    /// as the state it describes (issue #522) - so the person is free to join another business.
    /// </summary>
    /// <param name="businessId">
    /// The business the membership must belong to. It is paired with the membership key in the
    /// statement, so another business's member cannot be released by naming their key here.
    /// </param>
    /// <param name="membershipId">The membership to release, which the caller has read as active.</param>
    /// <param name="releasedAtUtc">The instant to record as the state change.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task ReleaseMemberAsync(
        int businessId,
        int membershipId,
        DateTime releasedAtUtc,
        CancellationToken cancellationToken);
}
