import {
  PLAN_PICKS_STORAGE_KEY,
  PLAN_PICKS_STORAGE_VERSION,
  PlanPicksScope,
  StoredPlanPicks,
  clearStoredPlanPicks,
  readStoredPlanPicks,
  writeStoredPlanPicks
} from './plan-picks-storage';
import { MAXIMUM_PICK_QUANTITY } from './plan-picks';

/**
 * This tab's Plan picks storage (issue #1021). Everything a stored plan is read back from is a
 * payload from outside the running application - it may be absent, truncated, hand-edited or
 * written by an older build - so these specs are mostly about what is refused, and about the three
 * ways a browser can simply say no.
 */

const scope: PlanPicksScope = { accountKey: 'account-1', businessName: 'Snack Co' };

function plan(overrides: Partial<StoredPlanPicks> = {}): StoredPlanPicks {
  return {
    appliedMachineIds: [10, 20],
    appliedProductIds: [1, 2],
    products: [
      {
        productId: 1,
        productName: 'Coke',
        mdbCode: 2,
        storageQuantityInStock: 6,
        totalQuantityToPick: 4,
        storageShortageQuantity: 0,
        machineQuantities: [{ machineId: 10, mdbCode: 2, currentQuantity: 6, targetQuantity: 10, quantityToPick: 4 }]
      }
    ],
    lastRefreshedAt: '2026-10-31T02:00:00.000Z',
    overrides: [{ key: '1:10', quantity: 9 }],
    pickedCells: ['1:10'],
    invalidQuantities: [{ key: '1:20', text: '1x', error: 'Enter a whole number of 0 or more.' }],
    ...overrides
  };
}

/** Writes a record straight into storage, the way a previous page load or an older build would have. */
function store(record: unknown): void {
  sessionStorage.setItem(PLAN_PICKS_STORAGE_KEY, JSON.stringify(record));
}

/** A valid record for `scope`, with `plan` replaced by whatever this test wants to try. */
function recordWithPlan(storedPlan: unknown): void {
  store({ version: PLAN_PICKS_STORAGE_VERSION, ...scope, plan: storedPlan });
}

describe('Plan picks tab storage (issue #1021)', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it('stores a plan and reads exactly it back for the same account and business', () => {
    expect(writeStoredPlanPicks(scope, plan())).toBe(true);

    expect(readStoredPlanPicks(scope)).toEqual(plan());
  });

  it('answers nothing when the tab holds no plan', () => {
    expect(readStoredPlanPicks(scope)).toBeNull();
  });

  it('replaces the plan it already held rather than keeping both', () => {
    writeStoredPlanPicks(scope, plan());
    writeStoredPlanPicks(scope, plan({ appliedMachineIds: [10] }));

    expect(readStoredPlanPicks(scope)?.appliedMachineIds).toEqual([10]);
  });

  it('never restores another account or another business, and leaves that record alone', () => {
    writeStoredPlanPicks(scope, plan());

    expect(readStoredPlanPicks({ ...scope, accountKey: 'account-2' })).toBeNull();
    expect(readStoredPlanPicks({ ...scope, businessName: 'Other Co' })).toBeNull();
    // The record is somebody else's to read, not this scope's to delete.
    expect(readStoredPlanPicks(scope)).toEqual(plan());
  });

  it('ignores a record written in another format version', () => {
    store({ version: PLAN_PICKS_STORAGE_VERSION + 1, ...scope, plan: plan() });

    expect(readStoredPlanPicks(scope)).toBeNull();
  });

  it('ignores and removes a record that is not readable JSON', () => {
    sessionStorage.setItem(PLAN_PICKS_STORAGE_KEY, 'not json {');

    expect(readStoredPlanPicks(scope)).toBeNull();
    expect(sessionStorage.getItem(PLAN_PICKS_STORAGE_KEY)).toBeNull();
  });

  it.each([
    ['no plan at all', undefined],
    ['a plan that is not an object', 'a plan'],
    ['no applied machines', plan({ appliedMachineIds: [] })],
    ['a machine id that is not a whole number', plan({ appliedMachineIds: [10.5] })],
    ['a machine id that is not a number', plan({ appliedMachineIds: ['10'] as unknown as number[] })],
    ['no products field', plan({ products: undefined as unknown as StoredPlanPicks['products'] })],
    ['a product with no name', plan({ products: [{ ...plan().products[0], productName: 7 as unknown as string }] })],
    [
      'a cell with no machine id',
      plan({
        products: [
          { ...plan().products[0], machineQuantities: [{ ...plan().products[0].machineQuantities[0], machineId: null as unknown as number }] }
        ]
      })
    ],
    ['an override key that is not a cell key', plan({ overrides: [{ key: '1-10', quantity: 2 }] })],
    ['a fractional override', plan({ overrides: [{ key: '1:10', quantity: 2.5 }] })],
    ['a negative override', plan({ overrides: [{ key: '1:10', quantity: -1 }] })],
    ['an override above the largest pick quantity', plan({ overrides: [{ key: '1:10', quantity: MAXIMUM_PICK_QUANTITY + 1 }] })],
    ['a ticked cell that is not a cell key', plan({ pickedCells: ['everything'] })],
    ['pending text with no message', plan({ invalidQuantities: [{ key: '1:10', text: '1x' } as never] })],
    ['an unreadable snapshot instant', plan({ lastRefreshedAt: 'the other day' })],
    ['no snapshot instant', plan({ lastRefreshedAt: undefined as unknown as string })]
  ])('ignores and removes a stored plan with %s', (_description, storedPlan) => {
    recordWithPlan(storedPlan);

    expect(readStoredPlanPicks(scope)).toBeNull();
    // One unreadable write must not go on answering "nothing" for the rest of the tab's life.
    expect(sessionStorage.getItem(PLAN_PICKS_STORAGE_KEY)).toBeNull();
  });

  it('keeps a snapshot figure the provider reported as negative, which is displayed and not planned', () => {
    const negative = plan({
      products: [{ ...plan().products[0], storageQuantityInStock: -3, machineQuantities: [{ ...plan().products[0].machineQuantities[0], currentQuantity: -1 }] }]
    });
    writeStoredPlanPicks(scope, negative);

    expect(readStoredPlanPicks(scope)).toEqual(negative);
  });

  it('reports a refused write and takes the plan it held with it', () => {
    writeStoredPlanPicks(scope, plan());
    const setItem = jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('The quota has been exceeded.', 'QuotaExceededError');
    });

    try {
      expect(writeStoredPlanPicks(scope, plan({ appliedMachineIds: [10] }))).toBe(false);
    } finally {
      setItem.mockRestore();
    }

    // Not the older plan presented as the newest saved work.
    expect(readStoredPlanPicks(scope)).toBeNull();
  });

  it('reads nothing, without throwing, from storage the browser refuses', () => {
    const getItem = jest.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Access to storage is denied.', 'SecurityError');
    });

    try {
      expect(readStoredPlanPicks(scope)).toBeNull();
    } finally {
      getItem.mockRestore();
    }
  });

  it('clears the plan, and survives storage that refuses even that', () => {
    writeStoredPlanPicks(scope, plan());
    clearStoredPlanPicks();
    expect(readStoredPlanPicks(scope)).toBeNull();

    const removeItem = jest.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Access to storage is denied.', 'SecurityError');
    });

    try {
      expect(() => clearStoredPlanPicks()).not.toThrow();
    } finally {
      removeItem.mockRestore();
    }
  });
});
