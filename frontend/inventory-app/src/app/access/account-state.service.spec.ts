import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { AccountStateAnswer, AccountStateService } from './account-state.service';

function account(homeAccountId: string): AccountInfo {
  return { homeAccountId, username: `${homeAccountId}@example.test` } as unknown as AccountInfo;
}

const MEMBER_RESPONSE = { state: 'Member', onboardingEnabled: false, rejectionReason: null };
const REVOKED_RESPONSE = { state: 'MembershipRevoked', onboardingEnabled: true, rejectionReason: null };

interface Harness {
  service: AccountStateService;
  get: jest.Mock;
  inProgress$: BehaviorSubject<InteractionStatus>;
  signIn(accountId: string | null): void;
  raise(eventType: EventType): void;
  observe(): AccountStateAnswer[];
}

/**
 * The service is constructed directly, with only its one `GET` doubled, for the same reason
 * `access.service.spec.ts` does: a stub that re-implemented the account-state answer would be
 * testing itself. This is a sibling cache to `AccessService`, built and invalidated the same way
 * (issue #529), so these tests mirror `access.service.spec.ts`'s rather than re-deriving each
 * trigger's rationale, which is documented there.
 */
function configure(
  options: {
    signedIn?: string | null;
    interaction?: InteractionStatus;
    responses?: (() => unknown)[];
  } = {}
): Harness {
  let signedIn = options.signedIn === undefined ? 'member-1' : options.signedIn;
  const responses = [...(options.responses ?? [])];
  const get = jest.fn(() => {
    const next = responses.shift();
    return next === undefined ? of(MEMBER_RESPONSE) : next();
  });
  const inProgress$ = new BehaviorSubject(options.interaction ?? InteractionStatus.None);
  const msalSubject$ = new Subject<EventMessage>();

  const service = new AccountStateService(
    { get } as unknown as HttpClient,
    { apiBaseUrl: '/api' } as unknown as ConfigService,
    {
      instance: {
        getActiveAccount: () => (signedIn === null ? null : account(signedIn)),
        getAllAccounts: () => (signedIn === null ? [] : [account(signedIn)])
      }
    } as unknown as MsalService,
    { inProgress$, msalSubject$ } as unknown as MsalBroadcastService
  );

  return {
    service,
    get,
    inProgress$,
    signIn: (accountId) => {
      signedIn = accountId;
    },
    raise: (eventType) => msalSubject$.next({ eventType } as EventMessage),
    observe: () => {
      const observed: AccountStateAnswer[] = [];
      service.state$.subscribe((answer) => observed.push(answer));
      return observed;
    }
  };
}

function refusal(status: number): HttpErrorResponse {
  return new HttpErrorResponse({ status });
}

function statuses(answers: AccountStateAnswer[]): string[] {
  return answers.map((answer) => answer.status);
}

describe('AccountStateService cache (issue #529)', () => {
  afterEach(() => jest.useRealTimers());

  it('reads GET /api/me/account-state once and keeps the state it reported', async () => {
    const { service, get } = configure();

    await firstValueFrom(service.state$);
    await firstValueFrom(service.state$);

    expect(get).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledWith('/api/me/account-state');
  });

  it('reports the state, onboarding eligibility and rejection reason the API named', async () => {
    const { service } = configure({
      responses: [() => of({ state: 'ApplicationRejected', onboardingEnabled: true, rejectionReason: 'Incomplete application' })]
    });

    const answer = await firstValueFrom(service.state$);

    expect(answer.status).toBe('known');
    if (answer.status !== 'known') {
      return;
    }
    expect(answer.accountState).toEqual({
      state: 'ApplicationRejected',
      onboardingEnabled: true,
      rejectionReason: 'Incomplete application'
    });
  });

  it('falls back to the most restrictive state for a value this build does not recognise', async () => {
    const { service } = configure({
      responses: [() => of({ state: 'SomeFutureState', onboardingEnabled: false, rejectionReason: null })]
    });

    const answer = await firstValueFrom(service.state$);

    expect(answer.status).toBe('known');
    if (answer.status !== 'known') {
      return;
    }
    expect(answer.accountState.state).toBe('MembershipAmbiguous');
  });

  it('waits for MSAL to finish any sign-in interaction before reading', async () => {
    const { service, get, inProgress$ } = configure({ interaction: InteractionStatus.HandleRedirect });
    const read = firstValueFrom(service.state$);
    expect(get).not.toHaveBeenCalled();

    inProgress$.next(InteractionStatus.None);

    await read;
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('asks nothing at all when nobody is signed in', async () => {
    const { service, get } = configure({ signedIn: null });

    const answer = await firstValueFrom(service.state$);

    expect(get).not.toHaveBeenCalled();
    expect(answer.status).toBe('unavailable');
  });

  it('reports unavailable when the read fails, rather than guessing a state', async () => {
    const { service } = configure({ responses: [() => throwError(() => refusal(500))] });

    const answer = await firstValueFrom(service.state$);

    expect(answer.status).toBe('unavailable');
  });

  it('publishes unread until the first answer arrives', () => {
    const pending = new Subject<unknown>();
    const { observe } = configure({ responses: [() => pending] });
    const observed = observe();

    expect(statuses(observed)).toEqual(['unread']);

    pending.next(MEMBER_RESPONSE);
    pending.complete();

    expect(statuses(observed)).toEqual(['unread', 'known']);
  });
});

describe('AccountStateService refresh triggers (issue #529)', () => {
  afterEach(() => jest.useRealTimers());

  it('re-reads on an explicit refresh and answers from the new state', async () => {
    const { service, get } = configure({ responses: [() => of(MEMBER_RESPONSE), () => of(REVOKED_RESPONSE)] });
    const first = await firstValueFrom(service.state$);
    expect(first.status === 'known' && first.accountState.state).toBe('Member');

    const refreshed = await firstValueFrom(service.refresh());

    expect(get).toHaveBeenCalledTimes(2);
    expect(refreshed.status === 'known' && refreshed.accountState.state).toBe('MembershipRevoked');
  });

  /**
   * A page whose every request is refused must not issue one account-state read per refusal. A
   * read already in flight answers with the server's current state, which is what that trigger is
   * asking for - the same coalescing `AccessService.refreshUnlessReading` does.
   */
  it('coalesces refreshUnlessReading while a read is still in flight', async () => {
    const pending = new Subject<unknown>();
    const { service, get, observe } = configure({ responses: [() => pending] });
    observe();
    expect(get).toHaveBeenCalledTimes(1);

    service.refreshUnlessReading();
    service.refreshUnlessReading();
    expect(get).toHaveBeenCalledTimes(1);

    pending.next(MEMBER_RESPONSE);
    pending.complete();
    service.refreshUnlessReading();

    expect(get).toHaveBeenCalledTimes(2);
  });

  it('re-reads when MSAL reports the active account changed', async () => {
    const { service, get, signIn, raise, observe } = configure({
      responses: [() => of(MEMBER_RESPONSE), () => of(REVOKED_RESPONSE)]
    });
    observe();
    await firstValueFrom(service.state$);

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(get).toHaveBeenCalledTimes(2);
  });

  it('re-reads on a sign-in after a read found nobody signed in', async () => {
    const { service, get, signIn, raise, observe } = configure({ signedIn: null });
    const observed = observe();
    expect(statuses(observed)).toEqual(['unavailable']);
    void service;

    signIn('member-1');
    raise(EventType.LOGIN_SUCCESS);

    expect(get).toHaveBeenCalledTimes(1);
  });

  it('clears the cache on sign-out without reading again', async () => {
    const { service, get, signIn, raise, observe } = configure();
    const observed = observe();
    await firstValueFrom(service.state$);

    signIn(null);
    raise(EventType.LOGOUT_SUCCESS);

    expect(get).toHaveBeenCalledTimes(1);
    expect(statuses(observed)).toEqual(['known', 'unread']);
  });

  it('re-reads on window focus once the answer is more than five minutes old', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure({ responses: [() => of(MEMBER_RESPONSE), () => of(REVOKED_RESPONSE)] });
    await firstValueFrom(service.state$);

    jest.setSystemTime(new Date('2026-10-12T09:05:01Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(2);
  });

  it('leaves a recent answer alone on window focus', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure();
    await firstValueFrom(service.state$);

    jest.setSystemTime(new Date('2026-10-12T09:04:59Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(1);
  });

  it('stops listening for window focus once it is destroyed', async () => {
    jest.useFakeTimers().setSystemTime(new Date('2026-10-12T09:00:00Z'));
    const { service, get } = configure();
    await firstValueFrom(service.state$);

    service.ngOnDestroy();
    jest.setSystemTime(new Date('2026-10-12T09:30:00Z'));
    window.dispatchEvent(new Event('focus'));

    expect(get).toHaveBeenCalledTimes(1);
  });

  it('discards an answer from a read a later refresh superseded', async () => {
    const slow = new Subject<unknown>();
    const { service, get, signIn, raise, observe } = configure({
      responses: [() => slow, () => of(MEMBER_RESPONSE)]
    });
    observe();

    signIn('member-2');
    raise(EventType.ACTIVE_ACCOUNT_CHANGED);
    expect(get).toHaveBeenCalledTimes(2);

    slow.next(REVOKED_RESPONSE);
    slow.complete();

    const answer = await firstValueFrom(service.state$);
    expect(answer.status === 'known' && answer.accountState.state).toBe('Member');
  });
});
