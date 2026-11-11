import { HttpErrorResponse } from '@angular/common/http';
import { Entitlement } from './entitlement';
import {
  PLAN_FEATURES,
  failsClosedWhileUnenforced,
  isDeclaredPlan,
  planFeatureAllowed,
  planRequiredOfError,
  requiredPlanOf
} from './plan-feature';

function entitlement(plan: string | null, enforced = true): Entitlement {
  return { enforced, plan, source: 'BillingSubscription', endsAtUtc: null };
}

describe('requiredPlanOf (issue #967/#979)', () => {
  it('mirrors PlanFeaturePolicy.RequiredPlan for every declared feature', () => {
    expect(requiredPlanOf('SalesProviderIntegration')).toBe('Pro');
    expect(requiredPlanOf('BookkeepingAndGstReports')).toBe('Starter');
    expect(requiredPlanOf('ProviderSalesReports')).toBe('Pro');
    expect(requiredPlanOf('SiteCommissions')).toBe('Pro');
    expect(requiredPlanOf('SalesFileImport')).toBe('Pro');
    expect(requiredPlanOf('ObservedSupplierPrices')).toBe('Starter');
    expect(requiredPlanOf('ReceiptScanning')).toBe('Pro');
  });
});

/**
 * `failsClosedWhileUnenforced` (issue #1163's one entry, read by the frontend since #1166): the
 * mirror of `PlanFeaturePolicy.FailsClosedWhileUnenforced`. Receipt scanning spends money at an
 * outside service per use, so there is no "nothing is enforced yet" state in which offering it is
 * the cautious answer - unlike every other feature, whose cost is already paid by running the
 * application at all.
 */
describe('failsClosedWhileUnenforced (issue #1163)', () => {
  it('names receipt scanning and nothing else', () => {
    expect(PLAN_FEATURES.filter(failsClosedWhileUnenforced)).toEqual(['ReceiptScanning']);
  });
});

describe('isDeclaredPlan', () => {
  it('declares exactly Free, Starter and Pro', () => {
    expect(isDeclaredPlan('Free')).toBe(true);
    expect(isDeclaredPlan('Starter')).toBe(true);
    expect(isDeclaredPlan('Pro')).toBe(true);
    expect(isDeclaredPlan('Enterprise')).toBe(false);
    expect(isDeclaredPlan('')).toBe(false);
  });
});

/**
 * `planFeatureAllowed` (issue #979): the frontend's mirror of `PlanFeaturePolicy.Allows`, which a
 * route guard or a menu entry decides a lock from without sending the request the backend would
 * refuse.
 */
describe('planFeatureAllowed', () => {
  it('fails open while the entitlement is not known', () => {
    for (const feature of PLAN_FEATURES) {
      expect(planFeatureAllowed(undefined, feature)).toBe(true);
    }
  });

  it('allows every feature whose cost running the application already pays while enforcement is switched off (issue #966)', () => {
    for (const feature of PLAN_FEATURES.filter((candidate) => !failsClosedWhileUnenforced(candidate))) {
      expect(planFeatureAllowed(entitlement('Free', false), feature)).toBe(true);
    }
  });

  /**
   * The one exception, and the reason the frontend mirrors the rule at all: offering a Scan receipt
   * button the endpoint will refuse while nothing is enforced is worse than not offering it.
   */
  it('refuses receipt scanning while enforcement is switched off (issue #1163)', () => {
    expect(planFeatureAllowed(entitlement(null, false), 'ReceiptScanning')).toBe(false);
    expect(planFeatureAllowed(entitlement('Pro', false), 'ReceiptScanning')).toBe(false);
  });

  it('still fails open for receipt scanning while the entitlement is not known at all', () => {
    expect(planFeatureAllowed(undefined, 'ReceiptScanning')).toBe(true);
  });

  it('allows receipt scanning only on Pro once enforcement is on', () => {
    expect(planFeatureAllowed(entitlement('Pro'), 'ReceiptScanning')).toBe(true);
    expect(planFeatureAllowed(entitlement('Starter'), 'ReceiptScanning')).toBe(false);
    expect(planFeatureAllowed(entitlement('Free'), 'ReceiptScanning')).toBe(false);
  });

  it('allows a feature whose required plan the business is on', () => {
    expect(planFeatureAllowed(entitlement('Starter'), 'BookkeepingAndGstReports')).toBe(true);
    expect(planFeatureAllowed(entitlement('Pro'), 'SalesProviderIntegration')).toBe(true);
  });

  it('allows a feature whose required plan the business has exceeded', () => {
    expect(planFeatureAllowed(entitlement('Pro'), 'BookkeepingAndGstReports')).toBe(true);
  });

  it('refuses a feature the business is below the required plan for', () => {
    expect(planFeatureAllowed(entitlement('Free'), 'BookkeepingAndGstReports')).toBe(false);
    expect(planFeatureAllowed(entitlement('Starter'), 'SalesProviderIntegration')).toBe(false);
  });

  /**
   * "Otherwise it fails closed" - `PlanFeaturePolicy.Allows`'s own rule for an entitlement that is
   * enforced but carries no plan, or one this build does not declare: there is no plan to compare
   * against, and guessing the nearest one would offer a feature nobody bought.
   */
  it('refuses every feature for an enforced entitlement with no plan', () => {
    for (const feature of PLAN_FEATURES) {
      expect(planFeatureAllowed(entitlement(null), feature)).toBe(false);
    }
  });

  it('refuses every feature for an enforced entitlement on an undeclared plan', () => {
    for (const feature of PLAN_FEATURES) {
      expect(planFeatureAllowed(entitlement('Enterprise'), feature)).toBe(false);
    }
  });
});

/**
 * `planRequiredOfError` (issue #979): the `403 plan_required` shape `InventoryApi.Auth.
 * PlanRequirement.Refusal` publishes for an inline optional part - `ObservedSupplierPrices`
 * (Starter), the summary this file names first because it is the only one an inline, rather than a
 * route-level, caller reads this from.
 */
describe('planRequiredOfError', () => {
  function forbidden(body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status: 403, error: body });
  }

  it('reads the required plan off a plan_required refusal', () => {
    expect(planRequiredOfError(forbidden({ code: 'plan_required', requiredPlan: 'Starter' }))).toBe('Starter');
    expect(planRequiredOfError(forbidden({ code: 'plan_required', requiredPlan: 'Pro' }))).toBe('Pro');
  });

  it('reads nothing out of another status, another code, or a plan it does not declare', () => {
    expect(planRequiredOfError(new HttpErrorResponse({ status: 409, error: { code: 'plan_required' } }))).toBeNull();
    expect(planRequiredOfError(forbidden({ code: 'read_only_support_session' }))).toBeNull();
    expect(planRequiredOfError(forbidden({ code: 'plan_required', requiredPlan: 'Enterprise' }))).toBeNull();
    expect(planRequiredOfError(forbidden({}))).toBeNull();
    expect(planRequiredOfError(forbidden(null))).toBeNull();
  });

  it('survives a body that is not an object and an error that merely looks like one', () => {
    expect(planRequiredOfError(forbidden('Forbidden'))).toBeNull();
    expect(planRequiredOfError({ status: 403, error: { code: 'plan_required', requiredPlan: 'Starter' } })).toBeNull();
    expect(planRequiredOfError(undefined)).toBeNull();
  });
});
