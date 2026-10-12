import { TestBed } from '@angular/core/testing';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { PlatformDiagnosticsAccessService } from './platform-diagnostics-access.service';
import { PlatformDiagnosticsAccess, PlatformDiagnosticsService } from './platform-diagnostics.service';

const account = { name: 'Platform Admin', username: 'admin@example.test' } as unknown as AccountInfo;

/** A second and third signed-in identity, to switch to; the key is the `homeAccountId`. */
function otherAccount(name: string): AccountInfo {
  return { name, homeAccountId: `${name}-home-id`, username: `${name}@example.test` } as unknown as AccountInfo;
}

const authorizedResponse: PlatformDiagnosticsAccess = {
  authorized: true,
  crossBusinessScope: true,
  limits: { maxSqlBytes: 16384, maxRows: 500, maxResponseBytes: 1048576, maxDurationSeconds: 5 }
};

function refusal(status: number): unknown {
  return { status, error: null };
}

interface Harness {
  service: PlatformDiagnosticsAccessService;
  accessFn: jest.Mock;
  interaction: BehaviorSubject<InteractionStatus>;
  /** MSAL's own account events, which is one of the two signals an identity change arrives on. */
  events: Subject<EventMessage>;
  /** Makes `next` the active account and notifies MSAL's subscribers, as a switch would. */
  switchTo(next: AccountInfo | null, eventType?: EventType): void;
  /** Changes the active account without notifying anybody, for the settling signal's own tests. */
  setActiveAccount(next: AccountInfo | null): void;
}

function configure(
  options: { signedIn?: boolean; access?: () => unknown; interaction?: InteractionStatus } = {}
): Harness {
  const accessFn = jest.fn(options.access ?? (() => of(authorizedResponse)));
  const interaction = new BehaviorSubject<InteractionStatus>(options.interaction ?? InteractionStatus.None);
  const events = new Subject<EventMessage>();
  let activeAccount: AccountInfo | null = (options.signedIn ?? true) ? account : null;

  TestBed.configureTestingModule({
    providers: [
      PlatformDiagnosticsAccessService,
      { provide: PlatformDiagnosticsService, useValue: { access: accessFn, query: jest.fn() } },
      {
        provide: MsalService,
        useValue: {
          instance: {
            getActiveAccount: () => activeAccount,
            getAllAccounts: () => (activeAccount === null ? [] : [activeAccount])
          }
        }
      },
      { provide: MsalBroadcastService, useValue: { inProgress$: interaction, msalSubject$: events } }
    ]
  });

  return {
    service: TestBed.inject(PlatformDiagnosticsAccessService),
    accessFn,
    interaction,
    events,
    switchTo(next: AccountInfo | null, eventType = EventType.ACTIVE_ACCOUNT_CHANGED): void {
      activeAccount = next;
      events.next({ eventType } as EventMessage);
    },
    setActiveAccount(next: AccountInfo | null): void {
      activeAccount = next;
    }
  };
}

/**
 * The diagnostics API is the only source of this answer (issue #335). There is deliberately no
 * frontend role, claim or flag that can grant it, because a second source could only diverge from
 * the configured Entra `(tid, oid)` identity the API resolves - and the page and both endpoints
 * stay independently authorized whatever this service answers.
 */
describe('PlatformDiagnosticsAccessService (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('grants the link only when the API confirms access', async () => {
    const { service } = configure();

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);
  });

  it('refuses when the API refuses the capability probe', async () => {
    const { service } = configure({ access: () => throwError(() => refusal(403)) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  it('refuses when the probe fails for any other reason, rather than assuming access', async () => {
    const { service } = configure({ access: () => throwError(() => new Error('network down')) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  /**
   * A 200 body always carries `authorized: true` - a refusal is a 403, not a body - so a body
   * saying otherwise is treated as a refusal rather than trusted.
   */
  it('refuses a 200 body that does not actually confirm access', async () => {
    const { service } = configure({ access: () => of({ ...authorizedResponse, authorized: false }) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  it('asks nothing at all when nobody is signed in', async () => {
    const { service, accessFn } = configure({ signedIn: false });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
    expect(accessFn).not.toHaveBeenCalled();
  });

  /**
   * A protected request issued while MSAL is still handling a redirect would have
   * `MsalInterceptor` start an interactive sign-in of its own, on the public `/auth` landing
   * route, for a menu entry nobody has asked for yet.
   */
  it('waits for MSAL to finish any sign-in interaction before probing', async () => {
    const { service, accessFn, interaction } = configure({ interaction: InteractionStatus.HandleRedirect });
    const granted = firstValueFrom(service.isGranted());
    expect(accessFn).not.toHaveBeenCalled();

    interaction.next(InteractionStatus.None);

    await expect(granted).resolves.toBe(true);
    expect(accessFn).toHaveBeenCalledTimes(1);
  });

  it('probes once however many times the answer is asked for', async () => {
    const { service, accessFn } = configure();

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);
    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);

    expect(accessFn).toHaveBeenCalledTimes(1);
  });
});

const authorizing = (): unknown => of(authorizedResponse);
const refusing = (): unknown => throwError(() => refusal(403));

/** One response per probe, in order; the last one answers every further probe. */
function queued(...responses: readonly (() => unknown)[]): () => unknown {
  let call = 0;
  return () => (responses[call++] ?? responses[responses.length - 1])();
}

/**
 * The answer belongs to the identity it was asked for (issue #605). One memoised probe for the
 * service's whole lifetime would carry the platform administrator's grant onto the next person who
 * signs in on the same browser, and an ordinary member's refusal onto the administrator - and in
 * neither direction may a late answer for an identity that is already gone decide what the current
 * one is shown.
 */
describe('PlatformDiagnosticsAccessService identity binding (issue #605)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('probes again as the newly active identity instead of replaying the previous grant', async () => {
    const harness = configure({ access: queued(authorizing, refusing) });

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    harness.switchTo(otherAccount('ordinary'));

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(false);
    expect(harness.accessFn).toHaveBeenCalledTimes(2);
  });

  it('lets a switch from an ordinary member to the administrator obtain a fresh grant', async () => {
    const harness = configure({ access: queued(refusing, authorizing) });

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(false);
    harness.switchTo(otherAccount('platform-admin'));

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    expect(harness.accessFn).toHaveBeenCalledTimes(2);
  });

  it('notices an identity change that only the settling interaction reports', async () => {
    const harness = configure({ access: queued(authorizing, refusing) });

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    harness.setActiveAccount(otherAccount('ordinary'));
    harness.interaction.next(InteractionStatus.None);

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(false);
    expect(harness.accessFn).toHaveBeenCalledTimes(2);
  });

  it('ignores answers for superseded identities across an A to B to C switch', async () => {
    const first = new Subject<PlatformDiagnosticsAccess>();
    const second = new Subject<PlatformDiagnosticsAccess>();
    const third = new Subject<PlatformDiagnosticsAccess>();
    const harness = configure({ access: queued(() => first, () => second, () => third) });
    const answer = firstValueFrom(harness.service.isGranted());

    harness.switchTo(otherAccount('b'));
    harness.switchTo(otherAccount('c'));
    first.next(authorizedResponse);
    second.next(authorizedResponse);
    third.next({ ...authorizedResponse, authorized: false });

    await expect(answer).resolves.toBe(false);
    expect(harness.accessFn).toHaveBeenCalledTimes(3);
  });

  it('cannot have a grant still in flight survive the sign-out that superseded it', async () => {
    const pending = new Subject<PlatformDiagnosticsAccess>();
    const harness = configure({ access: queued(() => pending) });
    const answer = firstValueFrom(harness.service.isGranted());

    harness.switchTo(null, EventType.LOGOUT_SUCCESS);
    pending.next(authorizedResponse);

    await expect(answer).resolves.toBe(false);
    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(false);
    expect(harness.accessFn).toHaveBeenCalledTimes(1);
  });

  it('asks nothing while signed out and probes once somebody signs in', async () => {
    const harness = configure({ signedIn: false });

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(false);
    expect(harness.accessFn).not.toHaveBeenCalled();

    harness.switchTo(otherAccount('platform-admin'), EventType.LOGIN_SUCCESS);

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    expect(harness.accessFn).toHaveBeenCalledTimes(1);
  });

  it('sends no probe for the new identity while a sign-in interaction is still running', async () => {
    const harness = configure({ access: queued(authorizing, authorizing) });

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    harness.interaction.next(InteractionStatus.HandleRedirect);
    harness.switchTo(otherAccount('platform-admin'));
    expect(harness.accessFn).toHaveBeenCalledTimes(1);

    harness.interaction.next(InteractionStatus.None);

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    expect(harness.accessFn).toHaveBeenCalledTimes(2);
  });

  it('drops a granted answer on the switch itself, before the new identity has been answered', async () => {
    const pending = new Subject<PlatformDiagnosticsAccess>();
    const harness = configure({ access: queued(authorizing, () => pending) });
    const seen: boolean[] = [];
    harness.service.granted$.subscribe((granted) => seen.push(granted));
    expect(seen).toEqual([true]);

    harness.switchTo(otherAccount('ordinary'));
    expect(seen).toEqual([true, false]);

    pending.next({ ...authorizedResponse, authorized: false });
    expect(seen).toEqual([true, false]);
  });

  it('reports the new identity\'s own grant to the menu once its probe answers', async () => {
    const pending = new Subject<PlatformDiagnosticsAccess>();
    const harness = configure({ access: queued(refusing, () => pending) });
    const seen: boolean[] = [];
    harness.service.granted$.subscribe((granted) => seen.push(granted));

    harness.switchTo(otherAccount('platform-admin'));
    pending.next(authorizedResponse);

    expect(seen).toEqual([false, true]);
  });

  it('drops a granted answer immediately on sign-out', async () => {
    const harness = configure();
    const seen: boolean[] = [];
    harness.service.granted$.subscribe((granted) => seen.push(granted));
    expect(seen).toEqual([true]);

    harness.switchTo(null, EventType.LOGOUT_SUCCESS);

    expect(seen).toEqual([true, false]);
  });

  it('treats a duplicate notification about the same identity as nothing to do', async () => {
    const harness = configure();

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    harness.events.next({ eventType: EventType.ACTIVE_ACCOUNT_CHANGED } as EventMessage);
    harness.events.next({ eventType: EventType.LOGIN_SUCCESS } as EventMessage);
    harness.interaction.next(InteractionStatus.None);

    await expect(firstValueFrom(harness.service.isGranted())).resolves.toBe(true);
    expect(harness.accessFn).toHaveBeenCalledTimes(1);
  });
});
