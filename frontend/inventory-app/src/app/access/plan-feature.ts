import { HttpErrorResponse } from '@angular/common/http';
import { Entitlement } from './entitlement';

/**
 * The frontend's own copy of the backend's declared plan-feature vocabulary
 * (`Inventory.Domain.Subscriptions.PlanFeature`/`PlanFeaturePolicy`, issue #967), mirrored rather
 * than derived for the same reason {@link CAPABILITIES} in `capabilities.ts` is: the names and the
 * plan each needs are a backend contract, and a client only ever asks what it already declares here.
 *
 * This is what a route guard or a menu entry (issue #979) decides a **route-level** lock from,
 * without sending the request that would only be refused. It is deliberately not used for the
 * per-feature Nayax reasons in `nayax-availability.ts` (`PlanRequired` beside `NayaxNotConnected`
 * and `PermissionNotGranted`): those arrive already decided, on the same response the figures they
 * are missing from would have been on, and every one of them is this business's sales-provider
 * connection or its site commissions - both the `Pro` plan - so that file states it directly rather
 * than looking it up here.
 */
export const PLAN_FEATURES = [
  'SalesProviderIntegration',
  'BookkeepingAndGstReports',
  'ProviderSalesReports',
  'SiteCommissions',
  'SalesFileImport',
  'ObservedSupplierPrices'
] as const;

/** One declared plan feature, spelled exactly as `PlanFeatures.Name` publishes it. */
export type PlanFeature = (typeof PLAN_FEATURES)[number];

/** The declared plan vocabulary, least entitled first - `Inventory.Domain.Subscriptions.SubscriptionPlan`. */
export const SUBSCRIPTION_PLANS = ['Free', 'Starter', 'Pro'] as const;

/** One declared plan name, spelled exactly as the API publishes `Entitlement.plan` and `requiredPlan`. */
export type SubscriptionPlanName = (typeof SUBSCRIPTION_PLANS)[number];

/**
 * The least plan that includes each feature - `PlanFeaturePolicy.RequiredPlan`'s one table, copied
 * rather than re-derived so a client cannot come to disagree with the endpoint it is about to call.
 */
const REQUIRED_PLAN: Readonly<Record<PlanFeature, SubscriptionPlanName>> = {
  SalesProviderIntegration: 'Pro',
  BookkeepingAndGstReports: 'Starter',
  ProviderSalesReports: 'Pro',
  SiteCommissions: 'Pro',
  SalesFileImport: 'Pro',
  ObservedSupplierPrices: 'Starter'
};

/** The least plan that includes `feature`, for the sentence a lock names it in. */
export function requiredPlanOf(feature: PlanFeature): SubscriptionPlanName {
  return REQUIRED_PLAN[feature];
}

function rankOf(plan: SubscriptionPlanName): number {
  return SUBSCRIPTION_PLANS.indexOf(plan);
}

/** Whether `plan` is one this build declares - never trust a string read off the wire further than this. */
export function isDeclaredPlan(plan: string): plan is SubscriptionPlanName {
  return (SUBSCRIPTION_PLANS as readonly string[]).includes(plan);
}

/**
 * Whether `entitlement` includes `feature` - the frontend's mirror of `PlanFeaturePolicy.Allows`,
 * for a route guard or a menu entry to decide **without** making the request the backend would
 * refuse (AGENTS.md § Nayax integration's sibling rule for plan refusals).
 *
 * **`undefined` is "not known", and it fails open**, exactly as an unreadable capability set does on
 * {@link AccessService}: nothing has been read, the read failed, or an older API did not report it,
 * and offering the feature is what lets the page's own call answer for itself rather than locking a
 * member out of a feature their business may perfectly well have bought.
 *
 * **Otherwise it is the backend's own rule.** `enforced: false` offers everything - "not enforced is
 * not a plan" - and once enforced, a `null` plan or one this build does not declare offers nothing
 * that is not free: there is no plan to compare against, and guessing the nearest one would offer a
 * feature nobody bought.
 */
export function planFeatureAllowed(entitlement: Entitlement | undefined, feature: PlanFeature): boolean {
  if (entitlement === undefined || !entitlement.enforced) {
    return true;
  }

  const plan = entitlement.plan;
  return plan !== null && isDeclaredPlan(plan) && rankOf(plan) >= rankOf(requiredPlanOf(feature));
}

/** `PlanRequirement.ErrorCode` (`InventoryApi.Auth.PlanRequirement`, issue #967): the `403` code. */
const PLAN_REQUIRED_ERROR_CODE = 'plan_required';

/**
 * The plan a `403 plan_required` refusal names, or `null` when the failure is not one - a transport
 * failure, a role's own `403`, anything else. `PlanRequirement.Refusal` publishes the required plan
 * on every refusal of this shape, so a client never has to look it up here: an inline optional part
 * (issue #979) that caught this refusal shows the plan the business was actually told, rather than
 * guessing from the feature it asked for.
 */
export function planRequiredOfError(error: unknown): SubscriptionPlanName | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 403) {
    return null;
  }

  const body: unknown = error.error;
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const { code, requiredPlan } = body as { code?: unknown; requiredPlan?: unknown };
  return code === PLAN_REQUIRED_ERROR_CODE && typeof requiredPlan === 'string' && isDeclaredPlan(requiredPlan)
    ? requiredPlan
    : null;
}
