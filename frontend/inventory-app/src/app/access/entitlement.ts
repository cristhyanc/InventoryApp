/**
 * What the business has *bought*, as `GET /api/me/access` reported it (issue #966).
 *
 * It travels with the capability set because the shell reads both from one answer, and it is
 * **not** one of them: a plan says what the business bought, a role says what this member may do
 * with it, and a usable Nayax connection is a third, separate condition. Which features a plan
 * withholds - and the locked menu entries and pages that go with them - is the enforcement work of
 * issues #967, #977 and #979; nothing in this release hides anything because of a plan.
 *
 * **`enforced: false` is today's behaviour and the shipped state.** While the rollout setting is
 * off, and while the grandfathering step has not completed, the API reports that nothing is enforced
 * and no plan at all - and a client offers everything, exactly as it does now. `plan` is `null`
 * there on purpose: "unrestricted" is not a plan anybody is on, and reading it as `Free` would lock
 * a business nothing is enforcing anything for.
 *
 * `undefined` wherever this type is accepted means the answer is **not known** - nothing has been
 * read, the read failed, or an API that does not report it answered - and then nothing is withheld.
 * That is the fail-open rule `AccessService` already applies to an unreadable capability set and to
 * the switchable option visibility beside it.
 */
export interface Entitlement {
  /**
   * Whether plan entitlement applies at all. `false` means every feature is offered, whatever
   * {@link plan} says.
   */
  readonly enforced: boolean;

  /**
   * The effective plan's published name while {@link enforced} is `true`, and `null` while it is
   * not. A name rather than a number, for the reason the role and the capabilities are names.
   */
  readonly plan: string | null;

  /**
   * Where that plan came from, as the API's published name: `EnforcementDisabled`,
   * `GrandfatheringNotCompleted`, `ManualGrant`, `BillingSubscription` or `NoActiveRecord`. It is
   * what lets a screen tell a complimentary or grandfathered plan from a paid one.
   */
  readonly source: string;

  /**
   * When the record that decided the plan stops entitling it, as the API reported it, or `null`
   * when it has no recorded end - which is what a grandfathered grant has until an owner sets one.
   */
  readonly endsAtUtc: string | null;

  /**
   * How much of the business's purchase and expense attachment allowance it is using (issue #968),
   * or `null` while nothing is enforced - because then there is no allowance, nothing is counted
   * and no upload is refused.
   *
   * It is `undefined` for an API that does not report it at all, which is the same "not known"
   * this whole type uses, and nothing is withheld on either answer.
   */
  readonly attachmentStorage?: AttachmentStorage | null;

  /**
   * What the business's connected machines cost it (issue #978), or `null` while nothing is
   * enforced - which is the shipped state, where nothing about the business's machines is read at
   * all. `undefined` means the answer is not known, by the same fail-open rule as the rest of this
   * type.
   */
  readonly connectedMachines?: ConnectedMachines | null;
}

/**
 * The business's attachment storage, as `GET /api/me/access` reported it beside the plan
 * (issue #968).
 *
 * It **withholds nothing**. At or over the allowance only new attachment *uploads* are refused:
 * viewing, downloading and deleting attachments, and recording purchases and expenses without a
 * document, keep working exactly as they do under it - and no attachment is ever deleted
 * automatically, including after a downgrade that leaves a business above its new allowance.
 */
export interface AttachmentStorage {
  /**
   * The recorded sizes of the attachments the business's purchases and expenses refer to, plus the
   * uploads it currently has in progress. A stored file no record refers to is not charged to the
   * business, and an attachment whose size was never recorded counts as nothing rather than as a
   * guess.
   */
  readonly usedBytes: number;

  /** The effective plan's allowance in bytes. 1 GB is 1,000,000,000 bytes. */
  readonly limitBytes: number;

  /**
   * The API's published warning name: `Normal` under 80% of the allowance, `Warning` from 80%, and
   * `Full` at or over it. A name rather than a percentage each screen would round its own way.
   */
  readonly warningLevel: string;
}

/**
 * The business's connected machines, as `GET /api/me/access` reported them beside the plan
 * (issue #978).
 *
 * A connected machine is a machine on the business's Nayax account, linked in this application or
 * not; a manual machine is never one. The count is **recorded evidence** rather than a live read -
 * the last complete fleet read of the business's current Nayax account - so it is reported with how
 * well known it is, and the page that shows any of this is #970's work; nothing in this release
 * displays it.
 *
 * **Being over the allowance is billed, never blocked.** No screen may read
 * {@link machinesOverAllowance} as a limit: linking a machine, importing sales, the reports and the
 * pick list are unaffected by it, and nothing removes a link or any history.
 */
export interface ConnectedMachines {
  /** How many connected machines the effective plan includes - five on Pro. */
  readonly includedByPlan: number;

  /** How many extra connected machines the business has bought. */
  readonly purchasedAdditional: number;

  /** {@link includedByPlan} plus {@link purchasedAdditional}: what the count is compared against. */
  readonly allowance: number;

  /**
   * The machines the current Nayax account was last counted at, or `null` when there is no count.
   * Null, never zero: a count nobody could take is not an empty fleet, so a client must not render
   * it as `0`.
   */
  readonly count: number | null;

  /** The UTC day that count was taken for, as the API reported it, or `null` when there is none. */
  readonly asOfDateUtc: string | null;

  /**
   * How many UTC days old the count is: `0` while it is current, its real age while it is stale,
   * and `null` when there is no count.
   */
  readonly ageInDays: number | null;

  /** How well known the count is: `Current`, `Stale` or `Unavailable`. */
  readonly state: string;

  /**
   * Why there is no count - `NotConnected`, `ConnectionNeedsAttention`, `NeverCounted` or
   * `AccountNotYetCounted` - and `None` when there is one.
   */
  readonly unavailableReason: string;

  /**
   * How many counted machines are above the allowance, never negative, and `null` when there is no
   * count - because "none over" and "not known" are different answers.
   */
  readonly machinesOverAllowance: number | null;
}
