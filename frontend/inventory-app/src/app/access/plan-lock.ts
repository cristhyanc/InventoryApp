import { Entitlement } from './entitlement';
import { PlanFeature, planFeatureAllowed, requiredPlanOf } from './plan-feature';

/**
 * What the business's entitlement (issue #966) means for one **destination** - a menu entry, its
 * route guard and the "needs a plan" page - as issue #979 asks for, in the same shape
 * `provider-lock.ts` gives the shared provider-availability signal for a destination's Nayax
 * dependency.
 *
 * **It is a translation of the entitlement, never a second opinion.** `planLockOf` is
 * {@link planFeatureAllowed} read the other way round, so the guard, the menu and the "needs a
 * plan" page can never reach three different verdicts about one destination from one answer.
 *
 * **There is one locked state, not two.** A provider dependency splits its refusal into "connect"
 * and "a permission on a connection that already works", because those ask an operator for
 * opposite things; a plan refusal always asks for the same thing - buy the plan that includes it -
 * so there is nothing here for a second state to distinguish.
 *
 * See docs/architecture.md § Plan-locked navigation.
 */

/** The address of the "needs a plan" page, declared once for the guard and the router. */
export const PLAN_LOCKED_PATH = 'needs-plan';

/**
 * The query parameter the guard uses to hand the locked page the URL it refused, exactly as
 * {@link PROVIDER_LOCKED_FROM_PARAM} does for a provider lock. It carries no decision: the page
 * resolves the destination from the declared navigation and re-reads the entitlement itself.
 */
export const PLAN_LOCKED_FROM_PARAM = 'from';

/** What one destination's plan dependency does to it right now. */
export type PlanLock =
  /** The entitlement includes the feature, or nothing is known yet: the destination behaves normally. */
  | 'open'
  /** The business's plan does not include the feature. */
  | 'locked';

/**
 * What one feature's entitlement means for a destination that needs it - {@link planFeatureAllowed}
 * with the answer turned into a destination state rather than a boolean.
 */
export function planLockOf(entitlement: Entitlement | undefined, feature: PlanFeature): PlanLock {
  return planFeatureAllowed(entitlement, feature) ? 'open' : 'locked';
}

/** The one short line a locked menu entry or shortcut carries, naming the destination and the plan. */
export function planLockSummary(feature: PlanFeature, destination: string): string {
  return `${destination} needs the ${requiredPlanOf(feature)} plan.`;
}

/**
 * The sentence the "needs a plan" page shows: what the destination needs and the one thing that
 * changes it - unlike a provider lock, never a Connect action, because buying a plan is not
 * something this application can do for an operator (issue #970 is the Plan page itself, and
 * billing is explicitly out of scope of issue #979).
 */
export function planLockSentence(feature: PlanFeature, destination: string): string {
  return (
    `${planLockSummary(feature, destination)} ` +
    `Anything this business has already saved or recorded is unaffected, and ${destination} opens again as soon as the business is on a plan that includes it.`
  );
}
