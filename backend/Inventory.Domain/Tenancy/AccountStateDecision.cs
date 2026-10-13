namespace Inventory.Domain.Tenancy;

/// <summary>
/// What <see cref="AccountStatePolicy"/> decided about one identity's own account: the state, and
/// the rejection reason that belongs to exactly one of those states (issue #540).
///
/// The reason travels with the state rather than beside it because the two must never be reported
/// independently: a reason without <see cref="AccountState.ApplicationRejected"/> would describe
/// an event that did not happen to this account, and the rejected state without its reason would
/// send the applicant to a screen that cannot say why. Returning them as one value means the
/// precedence decides both in one place.
/// </summary>
/// <param name="State">The caller's own account state.</param>
/// <param name="RejectionReason">
/// Why the business's application was refused, when <paramref name="State"/> is
/// <see cref="AccountState.ApplicationRejected"/> and the operator recorded a reason;
/// <see langword="null"/> for every other state, and for a rejection with no recorded reason.
/// </param>
public readonly record struct AccountStateDecision(AccountState State, string? RejectionReason);
