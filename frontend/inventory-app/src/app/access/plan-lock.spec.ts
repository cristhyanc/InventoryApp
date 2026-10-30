import { Entitlement } from './entitlement';
import { PLAN_LOCKED_FROM_PARAM, PLAN_LOCKED_PATH, planLockOf, planLockSentence, planLockSummary } from './plan-lock';

function entitlement(plan: string | null, enforced = true): Entitlement {
  return { enforced, plan, source: 'BillingSubscription', endsAtUtc: null };
}

describe('the declared "needs a plan" address', () => {
  it('is distinct from the provider-locked one', () => {
    expect(PLAN_LOCKED_PATH).toBe('needs-plan');
    expect(PLAN_LOCKED_FROM_PARAM).toBe('from');
  });
});

describe('planLockOf (issue #979)', () => {
  it('is open while the entitlement includes the feature', () => {
    expect(planLockOf(entitlement('Pro'), 'SalesProviderIntegration')).toBe('open');
  });

  it('is locked while it does not', () => {
    expect(planLockOf(entitlement('Starter'), 'SalesProviderIntegration')).toBe('locked');
  });

  it('is open while the entitlement is not known', () => {
    expect(planLockOf(undefined, 'SalesProviderIntegration')).toBe('open');
  });
});

describe('planLockSummary', () => {
  it('names the destination and the plan it needs', () => {
    expect(planLockSummary('SalesProviderIntegration', 'Sites')).toBe('Sites needs the Pro plan.');
    expect(planLockSummary('BookkeepingAndGstReports', 'Bookkeeping')).toBe('Bookkeeping needs the Starter plan.');
  });
});

describe('planLockSentence', () => {
  it('says what is unaffected and that the destination opens again once the plan includes it', () => {
    const sentence = planLockSentence('SiteCommissions', 'Site Commissions');

    expect(sentence).toContain('Site Commissions needs the Pro plan.');
    expect(sentence).toContain('unaffected');
    expect(sentence).toContain('Site Commissions opens again');
  });
});
