import { HttpErrorResponse } from '@angular/common/http';
import { EventType, InteractionStatus } from '@azure/msal-browser';
import { Subject, firstValueFrom, of, throwError } from 'rxjs';
import { AccessService, MemberAccessState } from './access.service';
import { CAPABILITIES, Capability } from './capabilities';
import {
  IdentityCacheHarness,
  apiConfig,
  describeIdentityChange,
  queuedReads,
  readingHttp,
  signedInIdentity
} from './identity-cache.spec-helpers';

const OWNER_RESPONSE = {
  role: 'Owner',
  capabilities: [...CAPABILITIES],
  businessName: 'Vending Co',
  timeZoneId: 'Australia/Sydney'
};

/** The Operator row of #328's table, written out rather than derived from the vocabulary. */
const OPERATOR_RESPONSE = {
  role: 'Operator',
  capabilities: ['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate'],
  businessName: 'Vending Co',
  timeZoneId: 'Australia/Sydney'
};

type Harness = IdentityCacheHarness<AccessService, MemberAccessState>;

/**
 * The service is constructed directly rather than through `TestBed`: it issues exactly one `GET`,
 * so a hand-written double keeps these tests about the caching and refresh rules rather than about
 * `HttpClient`. The doubles themselves are shared with the sibling cache's spec and with the
 * guard, sidebar and supplier specs that exercise the real service (`identity-cache.spec-helpers`).
 */
function configure(
  options: {
    signedIn?: string | null;
    interaction?: InteractionStatus;
    responses?: (() => unknown)[];
  } = {}
): Harness {
  const identity = signedInIdentity(options);
  const get = queuedReads(options.responses ?? [], OWNER_RESPONSE);
  const service = new AccessService(readingHttp(get), apiConfig(), identity.msal, identity.broadcast);

  return {
    service,
    get,
    inProgress$: identity.inProgress$,
    signIn: identity.signIn,
    raise: identity.raise,
    observe: () => {
      const observed: MemberAccessState[] = [];
      service.access$.subscribe((state) => observed.push(state));
      return observed;
    }
  };
}

function refusal(status: number): HttpErrorResponse {
  return new HttpErrorResponse({ status });
}

function statuses(states: MemberAccessState[]): string[] {
  return states.map((state) => state.status);
}

describe('AccessService capability cache (issue #528)', () => {
  afterEach(() => jest.useRealTimers());

  it('reads GET /api/me/access once and keeps the capabilities it reported', async () => {
    const { service, get } = configure();

    await expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(true);
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);

    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith('/api/me/access');
  });

  it('reports the role and the business the API named, and keeps only declared capabilities', async () => {
    const { service } = configure({
      responses: [() => of({ ...OPERATOR_RESPONSE, capabilities: ['Inventory.Operate', 'Nothing.Declared'] })]
    });

    const state = await firstValueFrom(service.access$);

    expect(state.status).toBe('granted');
    if (state.status !== 'granted') {
      return;
    }
    expect(state.access.role).toBe('Operator');
    expect(state.access.businessName).toBe('Vending Co');
    expect(state.access.timeZoneId).toBe('Australia/Sydney');
    expect([...state.access.capabilities]).toEqual(['Inventory.Operate']);
  });

  it('answers each capability from the reported set, granting nothing the role does not hold', async () => {
    const { service } = configure({ responses: [() => of(OPERATOR_RESPONSE)] });

    await expect(firstValueFrom(service.allows('Inventory.Operate'))).resolves.toBe(true);
    await expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(false);
    await expect(firstValueFrom(service.allows('Suppliers.ManageGstDefaults'))).resolves.toBe(false);
    await expect(firstValueFrom(service.allowsAny(['Data.Repair', 'Imports.Run']))).resolves.toBe(false);
    await expect(firstValueFrom(service.allowsAny(['Data.Repair', 'PickList.View']))).resolves.toBe(true);
  });

  /**
   * A protected request issued while MSAL is still handling a redirect would have
   * `MsalInterceptor` start an interactive sign-in of its own, on the public `/auth` landing
   * route, before anybody has asked for a page.
   */
  it('waits for MSAL to finish any sign-in interaction before reading', async () => {
    const { service, get, inProgress$ } = configure({ interaction: InteractionStatus.HandleRedirect });
    const allowed = firstValueFrom(service.allows('Dashboard.View'));
    expect(get).not.toHaveBeenCalled();

    inProgress$.next(InteractionStatus.None);

    await expect(allowed).resolves.toBe(true);
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('asks nothing at all when nobody is signed in, and hides nothing either', async () => {
    const { service, get } = configure({ signedIn: null });

    const state = await firstValueFrom(service.access$);

    expect(get).not.toHaveBeenCalled();
    expect(state.status).toBe('unavailable');
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);
  });

  /**
   * The fail-open rule. The endpoint behind every page checks the same capability itself, so a
   * capability set that could not be read must not hide a page from a member who may use it.
   */
  it('hides nothing when the read fails, rather than reporting an empty capability set', async () => {
    const { service } = configure({ responses: [() => throwError(() => refusal(500))] });

    const state = await firstValueFrom(service.access$);

    expect(state.status).toBe('unavailable');
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);
  });

  it('treats a refused read the same way, without reporting it to the operator', async () => {
    const { service } = configure({ responses: [() => throwError(() => refusal(403))] });

    await expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(true);
  });

  it('publishes unread until the first answer arrives, so a guard can tell the two apart', () => {
    const pending = new Subject<unknown>();
    const { observe } = configure({ responses: [() => pending] });
    const observed = observe();

    expect(statuses(observed)).toEqual(['unread']);

    pending.next(OWNER_RESPONSE);
    pending.complete();

    expect(statuses(observed)).toEqual(['unread', 'granted']);
  });
});

describe('AccessService refresh triggers (issue #528)', () => {
  afterEach(() => jest.useRealTimers());

  it('re-reads on an explicit refresh and answers from the new capabilities', async () => {
    const { service, get } = configure({ responses: [() => of(OPERATOR_RESPONSE), () => of(OWNER_RESPONSE)] });
    await expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(false);

    await firstValueFrom(service.refresh());

    expect(get).toHaveBeenCalledTimes(2);
    await expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(true);
  });

  /**
   * A page whose every request is refused must not issue one capability read per refusal. A read
   * already in flight answers with the server's current capabilities, which is exactly what that
   * trigger is asking for.
   */
  it('coalesces the refusal trigger while a read is still in flight', async () => {
    const pending = new Subject<unknown>();
    const { service, get, observe } = configure({ responses: [() => pending] });
    observe();
    expect(get).toHaveBeenCalledTimes(1);

    service.refreshUnlessReading();
    service.refreshUnlessReading();
    service.refreshUnlessReading();

    expect(get).toHaveBeenCalledTimes(1);

    pending.next(OWNER_RESPONSE);
    pending.complete();
    service.refreshUnlessReading();

    expect(get).toHaveBeenCalledTimes(2);
  });

  it('re-reads when MSAL reports the active account changed', async () => {
    const { service, get, signIn, raise, observe } = configure({
      responses: [() => of(OPERATOR_RESPONSE), () => of(OWNER_RESPONSE)]
    });
    observe();
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(false);

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(get).toHaveBeenCalledTimes(2);
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);
  });

  it('re-reads on a sign-in after a read found nobody signed in', async () => {
    const { service, get, signIn, raise, observe } = configure({ signedIn: null });
    const observed = observe();
    expect(statuses(observed)).toEqual(['unavailable']);

    signIn('member-1');
    raise(EventType.LOGIN_SUCCESS);

    expect(get).toHaveBeenCalledTimes(1);
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);
  });

  it('clears the cache on sign-out without reading again, because there is nobody to read it for', async () => {
    const { service, get, signIn, raise, observe } = configure();
    const observed = observe();
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);

    signIn(null);
    raise(EventType.LOGOUT_SUCCESS);

    expect(get).toHaveBeenCalledTimes(1);
    expect(statuses(observed)).toEqual(['granted', 'unread']);
  });

  it('leaves the cache alone while the signed-in identity is unchanged', async () => {
    const { service, get, raise, inProgress$, observe } = configure();
    observe();
    await firstValueFrom(service.allows('Data.Repair'));

    raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    raise(EventType.LOGIN_SUCCESS);
    inProgress$.next(InteractionStatus.None);

    expect(get).toHaveBeenCalledTimes(1);
  });

  it('re-reads on window focus once the answer is more than five minutes old', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure({ responses: [() => of(OPERATOR_RESPONSE), () => of(OWNER_RESPONSE)] });
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(false);

    jest.setSystemTime(new Date('2026-10-12T09:05:01Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(2);
    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(true);
  });

  it('leaves a recent answer alone on window focus', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure();
    await firstValueFrom(service.allows('Data.Repair'));

    jest.setSystemTime(new Date('2026-10-12T09:04:59Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(1);
  });

  it('stops listening for window focus once it is destroyed', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure();
    await firstValueFrom(service.allows('Data.Repair'));

    service.ngOnDestroy();
    jest.setSystemTime(new Date('2026-10-12T09:30:00Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(1);
  });

  /**
   * A read issued for the previous identity must never publish: its answer describes a member who
   * is no longer signed in, and the menu it would build is somebody else's.
   */
  it('discards an answer from a read a later refresh superseded', async () => {
    const slow = new Subject<unknown>();
    const { service, get, signIn, raise, observe } = configure({
      responses: [() => slow, () => of(OPERATOR_RESPONSE)]
    });
    observe();

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    expect(get).toHaveBeenCalledTimes(2);

    slow.next(OWNER_RESPONSE);
    slow.complete();

    await expect(firstValueFrom(service.allows('Data.Repair'))).resolves.toBe(false);
  });
});

/**
 * An actual change of signed-in identity owns the cache from the moment it happens (issue #603).
 * The previous identity's answer is not a weaker answer for the new one - it is somebody else's,
 * and publishing it for a moment is long enough for a guard or a menu to be built from it.
 *
 * The rule is the same on the sibling `AccountStateService`, so the suite itself lives in
 * `identity-cache.spec-helpers.ts` and both services are asserted against it.
 */
describeIdentityChange<AccessService, MemberAccessState>({
  subject: 'AccessService',
  answered: 'granted',
  firstResponse: OWNER_RESPONSE,
  secondResponse: OPERATOR_RESPONSE,
  configure: (responses) => configure({ responses }),
  // What `requireCapability` subscribes to, and an Operator does not hold `Reports.View`.
  guardAnswer: ({ service }) => expect(firstValueFrom(service.allows('Reports.View'))).resolves.toBe(false)
});

/**
 * What a long-lived UI subscriber is told across an identity change (issue #603). `allows` waits
 * for the first answer, which is what a guard needs; a page that is already rendering an action
 * the previous identity was granted needs the opposite - an answer that withdraws it, because the
 * `true` it received is otherwise the last thing it ever hears about somebody else's role.
 */
describe('AccessService capability answers across an identity change (issue #603)', () => {
  function observeAllows(service: AccessService, capability: Capability): boolean[] {
    const answers: boolean[] = [];
    service.allows(capability).subscribe((allowed) => answers.push(allowed));
    return answers;
  }

  it('withdraws the previous identity’s permission while the new identity’s read is pending', () => {
    const pending = new Subject<unknown>();
    const { service, signIn, raise } = configure({ responses: [() => of(OWNER_RESPONSE), () => pending] });
    const allowed = observeAllows(service, 'Suppliers.ManageGstDefaults');
    expect(allowed).toEqual([true]);

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(allowed).toEqual([true, false]);

    pending.next(OPERATOR_RESPONSE);
    pending.complete();

    // The new identity is an Operator, who does not hold it: the answer stays withdrawn.
    expect(allowed).toEqual([true, false]);
  });

  it('cannot have the previous identity’s late answer restore a withdrawn permission', () => {
    const stale = new Subject<unknown>();
    const { service, signIn, raise } = configure({
      responses: [() => of(OWNER_RESPONSE), () => stale, () => new Subject<unknown>()]
    });
    const allowed = observeAllows(service, 'Suppliers.ManageGstDefaults');
    // The previous identity's own re-read, still in flight when the identity changes.
    service.refresh();
    expect(allowed).toEqual([true]);

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    expect(allowed).toEqual([true, false]);

    stale.next(OWNER_RESPONSE);
    stale.complete();

    expect(allowed).toEqual([true, false]);
  });

  /**
   * The fail-open rule survives the withdrawal: `unavailable` is "asked and could not be
   * answered" for the identity signed in now, and hiding an action over a failed probe would take
   * it away from the member it belongs to.
   */
  it('allows the action again when the new identity’s read cannot be answered', () => {
    const { service, signIn, raise } = configure({
      responses: [() => of(OWNER_RESPONSE), () => throwError(() => refusal(500))]
    });
    const allowed = observeAllows(service, 'Suppliers.ManageGstDefaults');
    expect(allowed).toEqual([true]);

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(allowed).toEqual([true, false, true]);
  });

  /** A guard activating in the window still waits, rather than being refused the page. */
  it('answers a subscriber that arrives in the window only once the new identity has answered', async () => {
    const pending = new Subject<unknown>();
    const { service, signIn, raise, observe } = configure({
      responses: [() => of(OWNER_RESPONSE), () => pending]
    });
    observe();

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    const allowed = observeAllows(service, 'Suppliers.ManageGstDefaults');
    expect(allowed).toEqual([]);

    pending.next(OWNER_RESPONSE);
    pending.complete();

    expect(allowed).toEqual([true]);
  });
});

/** `allows` is live, so an action hidden by a stale answer reappears when a refresh allows it. */
describe('AccessService live capability answers (issue #528)', () => {
  it('emits again when a refresh changes the answer', async () => {
    const { service } = configure({ responses: [() => of(OPERATOR_RESPONSE), () => of(OWNER_RESPONSE)] });
    const answers: boolean[] = [];
    service.allows('Reports.View').subscribe((allowed) => answers.push(allowed));
    expect(answers).toEqual([false]);

    await firstValueFrom(service.refresh());

    expect(answers).toEqual([false, true]);
  });

  it('does not emit a repeated answer', async () => {
    const { service } = configure({ responses: [() => of(OPERATOR_RESPONSE), () => of(OPERATOR_RESPONSE)] });
    const answers: boolean[] = [];
    service.allows('Reports.View').subscribe((allowed) => answers.push(allowed));

    await firstValueFrom(service.refresh());

    expect(answers).toEqual([false]);
  });

  it('publishes a refreshed answer even when nothing is listening to that read', async () => {
    const { service, observe } = configure({ responses: [() => of(OPERATOR_RESPONSE), () => of(OWNER_RESPONSE)] });
    const observed = observe();

    service.refresh();

    expect(statuses(observed)).toEqual(['granted', 'granted']);
  });
});

/** The vocabulary the API's capability names have to keep matching (issue #521). */
describe('capability vocabulary (issue #521)', () => {
  it('declares the fifteen published Area.Action names in the order the API reports them', () => {
    expect([...CAPABILITIES]).toEqual([
      'Dashboard.View',
      'Dashboard.ViewFinancials',
      'PickList.View',
      'Inventory.Operate',
      'Purchasing.Operate',
      'Suppliers.ManageGstDefaults',
      'Expenses.Manage',
      'Reports.View',
      'Imports.Run',
      'SiteCommissions.Manage',
      'Integration.Manage',
      'Data.Repair',
      'Members.Manage',
      'Roles.View',
      'Business.Manage'
    ]);
  });
});
