using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// One member of the current business, as the member management of issue #533 reports them.
///
/// Four of the six fields are what an Owner needs to recognise a person and see what they may do;
/// the fifth is when the approval was recorded, and the sixth is the key the change endpoints
/// take. <see cref="DisplayName"/> and <see cref="Email"/> are the display-only personal data of
/// issue #524 and remain exactly that here: they are reported so a list shows people rather than
/// object ids, and nothing in this feature looks a member up by either of them.
///
/// What it deliberately does <em>not</em> carry is the member's Entra <c>(tid, oid)</c> pair and
/// the owning business id. That pair is the identity every access decision is made from, and
/// publishing it would put the one value that matters into a response a client could try to send
/// back; the business id is resolved from the caller's own membership and no response has a use
/// for it.
/// </summary>
/// <param name="MembershipId">
/// The membership's own persisted key, scoped to the current business by every endpoint that
/// accepts it: another business's key is simply not found, so it identifies a member only to
/// somebody who may already see them.
/// </param>
/// <param name="DisplayName">
/// The member's display name as their own validated sign-in token last reported it, or
/// <see langword="null"/> when no token has reported one - a member created by
/// <c>bootstrap-business</c> who has never signed in. Never a stand-in value: half a name, or an
/// object id presented as a name, is wrong rather than incomplete.
/// </param>
/// <param name="Email">The member's email address on the same terms; a mailbox to show, never an identifier.</param>
/// <param name="Role">
/// The member's role, or <see langword="null"/> when the stored value is one this code does not
/// declare - which only a hand-written <c>UPDATE</c> or a newer deployment can produce, and which
/// already denies that member access everywhere. It is reported as unknown rather than guessed at,
/// because naming a role nobody granted is how an Owner comes to believe somebody holds access
/// they do not.
/// </param>
/// <param name="IsActive">
/// Whether the approval is current. A revoked member keeps their row and stays on the list, so the
/// Owner can see who used to have access and reinstate them.
/// </param>
/// <param name="JoinedAtUtc">When the membership was created, which is what "joined" means here.</param>
public sealed record BusinessMember(
    int MembershipId,
    string? DisplayName,
    string? Email,
    BusinessRole? Role,
    bool IsActive,
    DateTime JoinedAtUtc);

/// <summary>
/// One membership as a pending change sees it (issue #533): the member it will be reported as, the
/// Entra identity the one-active-membership rule is re-checked for, and the role exactly as
/// persistence read it.
/// </summary>
/// <param name="Member">
/// The listed view of the same row, so a change can answer with the member it changed without a
/// second read.
/// </param>
/// <param name="Identity">
/// The member's Entra <c>(tid, oid)</c> pair, or <see langword="null"/> when the stored pair is not
/// a usable identity at all. Only a hand-written row can produce that, and it fails closed rather
/// than being reinstated without the eligibility check that pair is the subject of.
/// </param>
/// <param name="StoredRole">
/// The stored role as it is, declared or not. <see cref="BusinessMember.Role"/> above is the
/// published projection of the same value; this one is what a change is measured against and
/// audited as the "old" side of, so an undeclared value is visible in the audit trail instead of
/// being reported as no role at all.
/// </param>
public sealed record MemberMembership(
    BusinessMember Member,
    ActorIdentity? Identity,
    BusinessRole StoredRole);

/// <summary>
/// The state a member change intends to leave a membership in (issue #533). Both fields are always
/// stated, so an operation declares the whole resulting state rather than a delta, and
/// <see cref="MemberChangeWriter"/> writes only the columns that actually differ.
/// </summary>
/// <param name="Role">The role the membership should hold afterwards.</param>
/// <param name="IsActive">Whether the membership should be active afterwards.</param>
public readonly record struct MemberTargetState(BusinessRole Role, bool IsActive);
