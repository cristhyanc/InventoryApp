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
}
