import { ProviderConnection, ProviderFeatureAvailability } from './provider-availability.service';
import { PROVIDER_FEATURES } from './provider-feature-permissions';
import {
  PROVIDER_LOCKED_PATH,
  ProviderLock,
  providerFeatureReadLabels,
  providerLockOf,
  providerLockSentence,
  providerLockSummary,
  providerToolsOffered
} from './provider-lock';

/**
 * The menu's and the locked page's own rules (issue #729, child E2 of issue #329). They are pure
 * functions over the shared availability signal of issue #728, because the menu, the route guard and
 * the "needs a provider" page must reach the same verdict about one destination: a locked item whose
 * page then opened, or a page that locked an item the menu offered, is the failure this file exists
 * to prevent.
 */
describe('providerLockOf (issue #729)', () => {
  it('leaves a feature the shared signal offers open', () => {
    expect(providerLockOf('available')).toBe('open');
  });

  /**
   * `unknown` fails **open**, exactly as `providerFeatureOffered` does (issue #728): a status probe
   * that failed, and a usable connection nothing has yet stated a permission for, must not lock a
   * page whose own request would have answered it.
   */
  it('leaves an unknown answer open rather than locking a page nobody refused', () => {
    expect(providerLockOf('unknown')).toBe('open');
  });

  it('locks a feature the connection cannot serve, asking for a connection', () => {
    expect(providerLockOf('notConnected')).toBe('connect');
  });

  it('locks a refused feature as a permission, never as a missing connection', () => {
    expect(providerLockOf('permissionNotGranted')).toBe('permission');
  });

  it('answers every published availability state, so a new one cannot default to open', () => {
    const states: readonly ProviderFeatureAvailability[] = [
      'available',
      'unknown',
      'notConnected',
      'permissionNotGranted'
    ];

    expect(states.map(providerLockOf)).toEqual(['open', 'open', 'connect', 'permission']);
  });
});

/**
 * The provider repair and admin tools (issue #729): hidden until the provider is usable, because
 * there is nothing to repair or to import without one. `unknown` keeps them, for the same
 * fail-open reason a locked item does not lock on an unread status.
 */
describe('providerToolsOffered (issue #729)', () => {
  it('offers them on a usable connection', () => {
    expect(providerToolsOffered('usable')).toBe(true);
  });

  it('offers them while the connection state is not known', () => {
    expect(providerToolsOffered('unknown')).toBe(true);
  });

  it('withholds them with no connection and with credentials known not to work', () => {
    expect(providerToolsOffered('notConnected')).toBe(false);
    expect(providerToolsOffered('needsAttention')).toBe(false);
  });

  it('answers every published connection state', () => {
    const states: readonly ProviderConnection[] = ['usable', 'needsAttention', 'notConnected', 'unknown'];

    expect(states.map(providerToolsOffered)).toEqual([true, false, false, true]);
  });
});

/**
 * Naming what Nayax refused. The application knows the **reads** it declares
 * (`Inventory.Domain.Nayax.NayaxConnectionCheck`, worded once by `nayax-read-areas.ts`), and no
 * contract publishes the Nayax permission key behind one - so the message names the read and its
 * endpoint, which is what a support request has to ask for, and never a permission name nothing
 * published.
 */
describe('providerFeatureReadLabels (issue #729)', () => {
  it('names the declared read a live feature depends on, with its Nayax endpoint', () => {
    expect(providerFeatureReadLabels('machineFleet')).toEqual(['Machine list (GET machines)']);
  });

  it('names every read a feature needs, because a feature needing two reads needs both', () => {
    expect(providerFeatureReadLabels('productCatalogue')).toEqual([
      'Operator product catalogue (GET operators/{operatorId}/products)',
      'Operator product groups (GET operators/{operatorId}/productGroups)'
    ]);
  });

  /** `providerRecords` makes no live read at all, so no permission can be refused for it. */
  it('names no read for the feature that makes none', () => {
    expect(providerFeatureReadLabels('providerRecords')).toEqual([]);
  });
});

describe('providerLockSummary and providerLockSentence (issue #729)', () => {
  it('summarises a connect lock as needing Nayax, naming the destination', () => {
    const summary = providerLockSummary('connect', 'Machines');

    expect(summary).toContain('Machines');
    expect(summary.toLowerCase()).toContain('not connected');
  });

  /**
   * The permission summary says it is waiting for a Nayax permission - not that Nayax is
   * disconnected, which is the opposite diagnosis and would send an operator to re-enter working
   * credentials.
   */
  it('summarises a permission lock as waiting for a Nayax permission', () => {
    const summary = providerLockSummary('permission', 'Machines');

    expect(summary).toContain('Machines');
    expect(summary).toContain('waiting for a Nayax permission');
    expect(summary.toLowerCase()).not.toContain('not connected');
  });

  it('says nothing about an open destination', () => {
    expect(providerLockSummary('open', 'Machines')).toBe('');
  });

  it('names the refused read in the permission sentence', () => {
    const sentence = providerLockSentence('permission', 'Machines', 'machineFleet');

    expect(sentence).toContain('Machine list (GET machines)');
    expect(sentence).toContain('waiting for a Nayax permission');
  });

  /** A connect lock is about the connection, so it names no read: none of them was refused. */
  it('names no read in the connect sentence', () => {
    const sentence = providerLockSentence('connect', 'Machines', 'machineFleet');

    expect(sentence).not.toContain('GET machines');
    expect(sentence).toContain('Machines');
  });

  /** A live provider destination's connect sentence is unchanged by issue #868. */
  it('still says a live provider destination reads live data from Nayax', () => {
    const sentence = providerLockSentence('connect', 'Sites', 'machineFleet');

    expect(sentence).toContain('Sites is unavailable because Nayax is not connected.');
    expect(sentence).toContain('It reads live data from Nayax');
  });

  /**
   * Issue #868: the five reports locked on `providerRecords` read **stored** sales, so the sentence
   * must not claim they read live data - and must not leave an operator thinking the history they
   * already have went with the connection. It still asks for the one thing that unlocks the page, a
   * Nayax connection, so the page's heading and Connect action are the same.
   */
  it('words a stored-records destination without claiming it reads live data', () => {
    const sentence = providerLockSentence('connect', 'Daily', 'providerRecords');

    expect(sentence).toContain('Daily is unavailable because Nayax is not connected.');
    expect(sentence).not.toContain('reads live data');
    expect(sentence).toContain('Nothing stored has been removed');
    expect(sentence).toContain('a Nayax connection this application can use');
  });

  it('words a connect lock for every feature, so none of them renders as an empty sentence', () => {
    for (const feature of PROVIDER_FEATURES) {
      expect(providerLockSentence('connect', 'Daily', feature).length).toBeGreaterThan(0);
    }
  });

  it('claims no refused read when the feature declares none', () => {
    const sentence = providerLockSentence('permission', 'Stock History', 'providerRecords');

    expect(sentence).toContain('waiting for a Nayax permission');
    expect(sentence).not.toContain('GET');
  });

  it('words every lock state, so a state cannot render as an empty page', () => {
    const locks: readonly ProviderLock[] = ['connect', 'permission'];

    for (const lock of locks) {
      expect(providerLockSentence(lock, 'Machines', 'machineFleet').length).toBeGreaterThan(0);
    }
  });
});

describe('the locked page address (issue #729)', () => {
  it('is one declared path, so the guard and the menu cannot disagree about it', () => {
    expect(PROVIDER_LOCKED_PATH).toBe('needs-provider');
  });
});
