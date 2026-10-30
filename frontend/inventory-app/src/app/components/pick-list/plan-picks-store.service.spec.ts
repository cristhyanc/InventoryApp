import { MsalService } from '@azure/msal-angular';
import { BehaviorSubject } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../../access/access.service';
import { capabilitySet } from '../../access/capabilities';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import { PlanPicksRestoration, PlanPicksStoreService } from './plan-picks-store.service';
import { PLAN_PICKS_STORAGE_KEY, StoredPlanPicks } from './plan-picks-storage';

/**
 * Who may keep a Plan picks plan in this tab, and who may read one back (issue #1021). The plan
 * holds a business's product names and storage figures, so the three questions this service asks -
 * whose it is, whether the access is known, and whether this is an ordinary context - are the whole
 * of its behaviour.
 */

function access(businessName: string): MemberAccessState {
  const granted: MemberAccess = {
    role: 'Owner',
    capabilities: capabilitySet(['InventoryOperate']),
    businessName,
    timeZoneId: 'Australia/Sydney'
  };
  return { status: 'granted', access: granted };
}

function plan(overrides: Partial<StoredPlanPicks> = {}): StoredPlanPicks {
  return {
    appliedMachineIds: [10],
    appliedProductIds: [1],
    products: [
      {
        productId: 1,
        productName: 'Coke',
        mdbCode: null,
        storageQuantityInStock: 6,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [{ machineId: 10, mdbCode: null, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 }]
      }
    ],
    lastRefreshedAt: '2026-10-31T02:00:00.000Z',
    overrides: [{ key: '1:10', quantity: 9 }],
    pickedCells: [],
    invalidQuantities: [],
    ...overrides
  };
}

interface Harness {
  service: PlanPicksStoreService;
  accessState: BehaviorSubject<MemberAccessState>;
  sessionActive: BehaviorSubject<boolean>;
  previewActive: BehaviorSubject<boolean>;

  /** The one answer `restore()` gives, or `'no answer yet'` when it has not given one. */
  restore(): PlanPicksRestoration | null | 'no answer yet';
}

function createHarness(options: { businessName?: string; accountKey?: string | null } = {}): Harness {
  const accessState = new BehaviorSubject<MemberAccessState>(access(options.businessName ?? 'Snack Co'));
  const sessionActive = new BehaviorSubject(false);
  const previewActive = new BehaviorSubject(false);
  const homeAccountId: string | null = options.accountKey === undefined ? 'account-1' : options.accountKey;

  const service = new PlanPicksStoreService(
    {
      instance: {
        getActiveAccount: () => (homeAccountId === null ? null : { homeAccountId }),
        getAllAccounts: () => []
      }
    } as unknown as MsalService,
    { access$: accessState } as unknown as AccessService,
    { active$: sessionActive } as unknown as SupportSessionService,
    { active$: previewActive } as unknown as RolePreviewService
  );

  return {
    service,
    accessState,
    sessionActive,
    previewActive,
    restore: () => {
      let answer: PlanPicksRestoration | null | 'no answer yet' = 'no answer yet';
      service.restore().subscribe((restoration) => (answer = restoration));
      return answer;
    }
  };
}

describe('PlanPicksStoreService (issue #1021)', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it('stores a plan for the signed-in account and the business in view, and restores exactly it', () => {
    const harness = createHarness();

    expect(harness.service.save(plan())).toBe(true);

    expect(harness.restore()).toEqual({ scopeToken: 1, plan: plan() });
  });

  it('restores nothing when the tab holds nothing', () => {
    expect(createHarness().restore()).toBeNull();
  });

  it('clears the plan it holds', () => {
    const harness = createHarness();
    harness.service.save(plan());

    harness.service.clear();

    expect(harness.restore()).toBeNull();
  });

  it('never restores the plan of another business, even in the same tab', () => {
    const stored = createHarness({ businessName: 'Snack Co' });
    stored.service.save(plan());

    expect(createHarness({ businessName: 'Other Co' }).restore()).toBeNull();
  });

  it('never restores the plan of another signed-in account', () => {
    createHarness({ accountKey: 'account-1' }).service.save(plan());

    expect(createHarness({ accountKey: 'account-2' }).restore()).toBeNull();
  });

  it('waits for the capabilities before answering, and stores nothing until they are known', () => {
    const harness = createHarness();
    harness.accessState.next({ status: 'unread' });

    expect(harness.service.save(plan())).toBe(false);
    expect(harness.restore()).toBe('no answer yet');
    expect(sessionStorage.getItem(PLAN_PICKS_STORAGE_KEY)).toBeNull();

    harness.accessState.next(access('Snack Co'));
    expect(harness.restore()).toBeNull();
    expect(harness.service.save(plan())).toBe(true);
  });

  it('stores and restores nothing when the access could not be read at all', () => {
    const harness = createHarness();
    harness.service.save(plan());
    harness.accessState.next({ status: 'unavailable' });

    expect(harness.service.save(plan())).toBe(false);
    expect(harness.restore()).toBeNull();
  });

  it('stores and restores nothing while nobody is signed in', () => {
    const harness = createHarness({ accountKey: null });

    expect(harness.service.save(plan())).toBe(false);
    expect(harness.restore()).toBeNull();
  });

  it('stores and restores nothing during a support session', () => {
    const harness = createHarness();
    harness.service.save(plan());
    harness.sessionActive.next(true);

    expect(harness.service.save(plan({ appliedMachineIds: [20] }))).toBe(false);
    expect(harness.restore()).toBeNull();

    // The administrator's own context is restorable again once the session ends.
    harness.sessionActive.next(false);
    expect(harness.restore()).not.toBeNull();
  });

  it('stores and restores nothing during a role preview', () => {
    const harness = createHarness();
    harness.service.save(plan());
    harness.previewActive.next(true);

    expect(harness.service.save(plan({ appliedMachineIds: [20] }))).toBe(false);
    expect(harness.restore()).toBeNull();
  });

  it('stops vouching for a restoration once the business in view changes', () => {
    const harness = createHarness();
    harness.service.save(plan());
    const restoration = harness.restore() as PlanPicksRestoration;
    expect(harness.service.isCurrentScope(restoration.scopeToken)).toBe(true);

    harness.accessState.next(access('Other Co'));

    expect(harness.service.isCurrentScope(restoration.scopeToken)).toBe(false);
  });

  it('stops vouching for a restoration once a support session starts', () => {
    const harness = createHarness();
    harness.service.save(plan());
    const restoration = harness.restore() as PlanPicksRestoration;

    harness.sessionActive.next(true);

    expect(harness.service.isCurrentScope(restoration.scopeToken)).toBe(false);
  });

  it('stops vouching for a restoration once the capabilities are being read again', () => {
    const harness = createHarness();
    harness.service.save(plan());
    const restoration = harness.restore() as PlanPicksRestoration;

    // What a changed signed-in identity publishes before the new identity's own answer arrives.
    harness.accessState.next({ status: 'unread' });

    expect(harness.service.isCurrentScope(restoration.scopeToken)).toBe(false);
  });

  it('goes on vouching for a restoration while nothing about the scope has changed', () => {
    const harness = createHarness();
    harness.service.save(plan());
    const restoration = harness.restore() as PlanPicksRestoration;

    // The same answer again - a re-read of the capabilities that changed nothing.
    harness.accessState.next(access('Snack Co'));

    expect(harness.service.isCurrentScope(restoration.scopeToken)).toBe(true);
  });
});
