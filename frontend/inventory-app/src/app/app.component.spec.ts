import { HttpClient } from '@angular/common/http';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, NEVER, Observable, Subject, of } from 'rxjs';
import { AccessService, MemberAccessState } from './access/access.service';
import { AccountStateAnswer, AccountStateService } from './access/account-state.service';
import { CAPABILITIES, capabilitySet } from './access/capabilities';
import { signedInAccountMayHaveChanged } from './access/signed-in-account';
import { AppComponent } from './app.component';
import { BusinessTimeZoneService } from './formatting/business-time-zone.service';
import { ConfigService } from './services/config.service';
import { PlatformDiagnosticsAccessService } from './services/platform-diagnostics-access.service';
import { BusinessService, CurrentBusiness } from './services/business.service';

@Component({ standalone: true, template: 'page body' })
class BlankPageComponent {}

const account = { name: 'Dana Operator', username: 'dana@example.test' } as unknown as AccountInfo;

/** The identity of a signed-in account, as both the shell and the identity caches read it. */
function accountKeyOf(account: AccountInfo | null): string | null {
  if (account === null) {
    return null;
  }
  return account.homeAccountId || account.localAccountId || account.username || '';
}

interface MediaQueryStub {
  emit(matches: boolean): void;
}

function stubMatchMedia(matches: boolean): MediaQueryStub {
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  const list = {
    matches,
    media: '(min-width: 1024px)',
    addEventListener: (_type: string, listener: (event: MediaQueryListEvent) => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: (event: MediaQueryListEvent) => void) => listeners.delete(listener)
  };

  window.matchMedia = (() => list) as unknown as typeof window.matchMedia;

  return {
    emit(next: boolean): void {
      list.matches = next;
      for (const listener of listeners) {
        listener({ matches: next } as MediaQueryListEvent);
      }
    }
  };
}

interface Rendered {
  fixture: ComponentFixture<AppComponent>;
  host: HTMLElement;
  msal: {
    loginRedirect: jest.Mock;
    logoutRedirect: jest.Mock;
  };
  businessLoad: jest.Mock;
  businessReset: jest.Mock;
  /** Every `GET /api/business/current` the real lookup issued; only used with `businessHttp`. */
  businessGet: jest.Mock;
  /** The zone holder the real lookup publishes into; only meaningful with `businessHttp`. */
  timeZone: BusinessTimeZoneService;
  /** Re-runs the shell's sign-in check, as MSAL does when an interaction settles. */
  settleInteraction: () => void;
  /** Raises one MSAL account event, leaving the interaction status where it is (issue #604). */
  raiseAccountEvent: (eventType: EventType) => void;
  /** Answers the account-state read that is still pending (issue #604). */
  answerAccountState: (answer: AccountStateAnswer) => void;
  /** The identity of every account-state read, in order (issue #604). */
  accountStateReads: readonly string[];
}

const MEMBER_ACCOUNT_STATE: AccountStateAnswer = {
  status: 'known',
  accountState: { state: 'Member', onboardingEnabled: false, rejectionReason: null }
};

const UNREAD_ACCOUNT_STATE: AccountStateAnswer = { status: 'unread' };

/**
 * `AccountStateService` as the shell consumes it, obeying issue #603's identity-change contract.
 *
 * The real service is a root service the shell injects, so it is constructed - and subscribed to
 * MSAL's two signals - before the component exists. This double is built the same way, from the
 * same `signedInAccountMayHaveChanged` trigger, so a changed identity publishes `unread` again
 * before the shell's own handler runs and the new identity's answer arrives only when the test
 * supplies it. That ordering is the whole point: the shell must never act on the previous
 * identity's `Member` (or `NoMembership`) answer.
 */
class AccountStateDouble {
  readonly state$ = new BehaviorSubject<AccountStateAnswer>(UNREAD_ACCOUNT_STATE);

  /** The identity of every read this double issued, in order. */
  readonly reads: string[] = [];

  private cachedFor: string | null;

  constructor(
    broadcast: MsalBroadcastService,
    private readonly identity: () => string | null,
    private readonly answerFor: (identity: string) => AccountStateAnswer | 'pending'
  ) {
    this.cachedFor = this.identity();
    this.issueRead();
    signedInAccountMayHaveChanged(broadcast).subscribe(() => this.applySignedInAccount());
  }

  /** Answers the pending read, as the real service's own `GET` answering does. */
  answer(answer: AccountStateAnswer): void {
    this.state$.next(answer);
  }

  private applySignedInAccount(): void {
    const identity = this.identity();
    if (identity === this.cachedFor) {
      return;
    }

    this.cachedFor = identity;
    // Issue #603: the previous identity's answer is dropped before the new read is even waited for.
    this.state$.next(UNREAD_ACCOUNT_STATE);
    this.issueRead();
  }

  private issueRead(): void {
    if (this.cachedFor === null) {
      // A sign-out clears the answer without an authenticated read.
      return;
    }

    this.reads.push(this.cachedFor);
    const answer = this.answerFor(this.cachedFor);
    if (answer !== 'pending') {
      this.state$.next(answer);
    }
  }
}

async function render(
  options: {
    signedIn?: boolean;
    business?: () => Observable<CurrentBusiness | null>;
    /**
     * Answers for the **real** `BusinessService` instead of the stubbed one, in read order: what
     * proves which business zone the shell actually publishes across an account switch (#604).
     */
    businessHttp?: readonly (() => Observable<CurrentBusiness>)[];
    /** The active account, read on every sign-in check; defaults to `signedIn`. */
    activeAccount?: () => AccountInfo | null;
    /** Defaults to `Member`, so the existing business-lookup tests are unaffected (issue #529). */
    accountState?: AccountStateAnswer;
    /** Per-identity account-state answers; `'pending'` holds the read unanswered (issue #604). */
    accountStateFor?: (identity: string) => AccountStateAnswer | 'pending';
  } = {}
): Promise<Rendered> {
  const signedIn = options.signedIn ?? true;
  const activeAccount = options.activeAccount ?? (() => (signedIn ? account : null));
  const businessLoad = jest.fn(options.business ?? (() => of({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' })));
  const businessReset = jest.fn();
  const queuedBusinessReads = [...(options.businessHttp ?? [])];
  const businessGet = jest.fn(() => {
    const next = queuedBusinessReads.shift();
    return next === undefined ? (NEVER as Observable<CurrentBusiness>) : next();
  });
  const inProgress$ = new BehaviorSubject(InteractionStatus.None);
  const msalSubject$ = new Subject<EventMessage>();
  const broadcast = { inProgress$, msalSubject$ } as unknown as MsalBroadcastService;
  const loginRedirect = jest.fn();
  const logoutRedirect = jest.fn();
  const msalService = {
    instance: {
      getActiveAccount: () => activeAccount(),
      getAllAccounts: () => {
        const active = activeAccount();
        return active ? [active] : [];
      },
      setActiveAccount: jest.fn()
    },
    handleRedirectObservable: () => of(null),
    loginRedirect,
    logoutRedirect
  };

  const constantAnswer = options.accountState ?? MEMBER_ACCOUNT_STATE;
  const accountState = new AccountStateDouble(
    broadcast,
    () => accountKeyOf(activeAccount()),
    options.accountStateFor ?? (() => constantAnswer)
  );

  await TestBed.configureTestingModule({
    imports: [AppComponent],
    providers: [
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      { provide: MsalService, useValue: msalService },
      { provide: MsalBroadcastService, useValue: broadcast },
      // The shell renders the sidebar, which asks the diagnostics API whether to offer the
      // super-admin link (issue #335). The shell itself owns no part of that decision, so the
      // probe is stubbed as refused here and tested where it belongs.
      { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(false) } },
      // The sidebar also renders the capabilities GET /api/me/access reported (issue #528). The
      // shell owns no part of that decision either, so it is stubbed as every capability here and
      // tested in access.service.spec.ts and sidebar-nav.component.spec.ts.
      {
        provide: AccessService,
        useValue: {
          access$: of<MemberAccessState>({
            status: 'granted',
            access: {
              role: 'Owner',
              capabilities: capabilitySet([...CAPABILITIES]),
              businessName: 'Vending Co',
              timeZoneId: 'Australia/Sydney'
            }
          })
        }
      },
      // The shell reads the signed-in operator's business so every instant on the page can be
      // rendered in that business's time zone (issue #499). The shell owns only the waiting; the
      // lookup itself is tested in business.service.spec.ts. The zone tests of issue #604 instead
      // use the real service over an HttpClient double, because which zone is published across an
      // account switch is exactly what the real reset/generation protection decides.
      ...(options.businessHttp === undefined
        ? [{ provide: BusinessService, useValue: { load: businessLoad, reset: businessReset } }]
        : [
            { provide: HttpClient, useValue: { get: businessGet } },
            { provide: ConfigService, useValue: { apiBaseUrl: '/api' } }
          ]),
      // The shell waits for the account state before looking up a business at all (issue #529);
      // that gating is what app.component business-context tests below exercise, so the answer
      // comes from the double above rather than through the real cached service.
      { provide: AccountStateService, useValue: { state$: accountState.state$ } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AppComponent);
  fixture.detectChanges();

  return {
    fixture,
    host: fixture.nativeElement as HTMLElement,
    msal: { loginRedirect, logoutRedirect },
    businessLoad,
    businessReset,
    businessGet,
    timeZone: TestBed.inject(BusinessTimeZoneService),
    settleInteraction: () => {
      inProgress$.next(InteractionStatus.None);
      fixture.detectChanges();
    },
    raiseAccountEvent: (eventType: EventType) => msalSubject$.next({ eventType } as EventMessage),
    answerAccountState: (answer: AccountStateAnswer) => accountState.answer(answer),
    accountStateReads: accountState.reads
  };
}

/**
 * The one control that shows or hides the navigation, wherever the current layout puts it
 * (issue #456): the sidebar's own header row on a wide layout, the top bar on a narrow one. Both
 * carry `aria-controls` pointing at the navigation landmark, which is what makes this one query.
 */
function sidebarToggle(host: HTMLElement): HTMLButtonElement {
  const toggles = host.querySelectorAll<HTMLButtonElement>('button[aria-controls="primary-navigation"]');
  expect(toggles).toHaveLength(1);
  return toggles[0];
}

function headerToggle(host: HTMLElement): HTMLButtonElement | null {
  return host.querySelector<HTMLButtonElement>('header button[aria-controls="primary-navigation"]');
}

function sidebarHeaderToggle(host: HTMLElement): HTMLButtonElement | null {
  return host.querySelector<HTMLButtonElement>('nav[aria-label="Primary"] button[aria-controls="primary-navigation"]');
}

function sidebar(host: HTMLElement): HTMLElement | null {
  return host.querySelector<HTMLElement>('nav[aria-label="Primary"]');
}

const originalMatchMedia = window.matchMedia;

afterEach(() => {
  window.matchMedia = originalMatchMedia;
});

describe('AppComponent shell (issue #391)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('replaces the horizontal header navigation with a single left sidebar', async () => {
    const { host } = await render();

    expect(sidebar(host)).not.toBeNull();
    expect(host.querySelectorAll('nav[aria-label="Primary"]')).toHaveLength(1);
    expect(host.querySelector('header nav')).toBeNull();
    expect(host.querySelectorAll('details')).toHaveLength(0);
    expect(host.querySelector('header a[href="/categories"]')).toBeNull();
    expect(host.querySelector('header a[href="/reports/gst"]')).toBeNull();
  });

  it('renders the routed page in a main region beside the sidebar', async () => {
    const { host } = await render();

    const main = host.querySelector('main');
    expect(main).not.toBeNull();
    expect(main?.querySelector('router-outlet')).not.toBeNull();
    expect(main?.contains(sidebar(host) as Node)).toBe(false);
  });

  it('exposes the collapse control with aria-expanded and the sidebar it controls', async () => {
    const { host } = await render();
    const toggle = sidebarToggle(host);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-label')).toBeTruthy();
    expect(host.querySelector(`#${toggle.getAttribute('aria-controls')}`)).not.toBeNull();
  });
});

/**
 * The desktop toggle moved out of the top bar and into the sidebar's own header row, beside the
 * app title (issue #456). The shell keeps the single `isSidebarOpen` flag and every handler it
 * already had; only which element the operator presses changed, and on a narrow layout nothing
 * changed at all because the drawer has no header row on the page to put a control in.
 */
describe('AppComponent sidebar toggle placement (issue #456)', () => {
  it('puts the only wide-layout toggle in the sidebar header and leaves none detached in the top bar', async () => {
    stubMatchMedia(true);
    const { host } = await render();

    expect(headerToggle(host)).toBeNull();
    expect(sidebarHeaderToggle(host)).not.toBeNull();
    expect(sidebarHeaderToggle(host)?.getAttribute('aria-label')).toBe('Collapse navigation');
    expect(host.querySelector('nav[aria-label="Primary"]')?.id).toBe('primary-navigation');
  });

  it('collapses and reopens the wide sidebar from that one control', async () => {
    stubMatchMedia(true);
    const { fixture, host } = await render();

    sidebarHeaderToggle(host)?.click();
    fixture.detectChanges();

    const collapsed = sidebarHeaderToggle(host);
    expect(collapsed).not.toBeNull();
    expect(collapsed?.getAttribute('aria-expanded')).toBe('false');
    expect(collapsed?.getAttribute('aria-label')).toBe('Expand navigation');
    expect(sidebar(host)?.querySelector('.sr-only')?.textContent?.trim()).toBe('Dashboard');

    collapsed?.click();
    fixture.detectChanges();

    expect(sidebarHeaderToggle(host)?.getAttribute('aria-expanded')).toBe('true');
    expect(sidebar(host)?.querySelector('.sr-only')).toBeNull();
  });

  it('keeps the opener in the top bar on a narrow layout, where the sidebar is not on the page', async () => {
    stubMatchMedia(false);
    const { fixture, host } = await render();

    const opener = headerToggle(host);
    expect(opener).not.toBeNull();
    expect(opener?.getAttribute('aria-label')).toBe('Open navigation menu');
    expect(opener?.getAttribute('aria-expanded')).toBe('false');

    opener?.click();
    fixture.detectChanges();

    expect(sidebar(host)).not.toBeNull();
    expect(headerToggle(host)?.getAttribute('aria-label')).toBe('Close navigation menu');
    // The drawer closes; it never offers a collapse control, because a drawer has no rail state.
    expect(sidebarHeaderToggle(host)).toBeNull();
    expect(host.querySelector('nav[aria-label="Primary"] button[aria-label="Close navigation menu"]')).not.toBeNull();
  });

  // JSDOM computes no layout, so the 44x44 CSS-pixel minimum is asserted on the sizing utilities
  // that produce it: `h-11`/`w-11` are 2.75rem = 44px.
  it('gives the narrow-layout opener the same 44x44 touch target as the sidebar control', async () => {
    stubMatchMedia(false);
    const { host } = await render();
    const classes = Array.from(headerToggle(host)?.classList ?? []);

    expect(classes).toContain('h-11');
    expect(classes).toContain('w-11');
  });

  it('reuses the shell state, so the relocated control changes no routing or navigation availability', async () => {
    stubMatchMedia(true);
    const { fixture, host } = await render();
    const destinations = Array.from(host.querySelectorAll('nav[aria-label="Primary"] a')).map((anchor) =>
      anchor.getAttribute('href')
    );

    sidebarHeaderToggle(host)?.click();
    fixture.detectChanges();

    expect(
      Array.from(host.querySelectorAll('nav[aria-label="Primary"] a')).map((anchor) => anchor.getAttribute('href'))
    ).toEqual(destinations);

    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();

    expect(host.querySelector('main')?.textContent).toContain('page body');
  });
});

describe('AppComponent wide layout (issue #391)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('starts expanded and collapses to a compact sidebar that stays visible', async () => {
    const { fixture, host } = await render();
    const toggle = sidebarToggle(host);
    expect(sidebar(host)?.textContent).toContain('Dashboard');
    expect(sidebar(host)?.querySelector('.sr-only')).toBeNull();

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(sidebar(host)).not.toBeNull();
    expect(sidebar(host)?.querySelector('.sr-only')?.textContent?.trim()).toBe('Dashboard');

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(sidebar(host)?.querySelector('.sr-only')).toBeNull();
  });

  it('expands again when a collapsed group heading is used', async () => {
    const { fixture, host } = await render();
    sidebarToggle(host).click();
    fixture.detectChanges();

    const reports = Array.from(host.querySelectorAll('button')).find((button) =>
      (button.textContent ?? '').includes('Reports')
    );
    reports?.click();
    fixture.detectChanges();

    expect(sidebarToggle(host).getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('a[href="/reports/gst"]')).not.toBeNull();
  });

  it('keeps the sidebar open after navigating, and shows no dismiss control or backdrop', async () => {
    const { fixture, host } = await render();

    host.querySelector<HTMLAnchorElement>('a[href="/pick-list"]')?.click();
    fixture.detectChanges();

    expect(sidebarToggle(host).getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector('button[aria-label="Close navigation menu"]')).toBeNull();
  });
});

describe('AppComponent narrow layout (issue #391)', () => {
  beforeEach(() => stubMatchMedia(false));

  it('starts with the sidebar dismissed and recreates no horizontal menu', async () => {
    const { host } = await render();

    expect(sidebar(host)).toBeNull();
    expect(sidebarToggle(host).getAttribute('aria-expanded')).toBe('false');
    expect(host.querySelector('header nav')).toBeNull();
  });

  it('opens a dismissible drawer from the burger control', async () => {
    const { fixture, host } = await render();
    sidebarToggle(host).click();
    fixture.detectChanges();

    expect(sidebar(host)).not.toBeNull();
    expect(sidebar(host)?.querySelector('.sr-only')).toBeNull();
    const dismissControls = host.querySelectorAll('button[aria-label="Close navigation menu"]');
    expect(dismissControls.length).toBeGreaterThan(0);

    (dismissControls[0] as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(sidebar(host)).toBeNull();
  });

  it('dismisses the drawer on Escape and returns focus to the burger control', async () => {
    const { fixture, host } = await render();
    const toggle = sidebarToggle(host);
    toggle.click();
    fixture.detectChanges();
    expect(sidebar(host)).not.toBeNull();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(sidebar(host)).toBeNull();
    expect(document.activeElement).toBe(toggle);
  });

  it('dismisses the drawer once a destination is chosen', async () => {
    const { fixture, host } = await render();
    sidebarToggle(host).click();
    fixture.detectChanges();

    host.querySelector<HTMLAnchorElement>('a[href="/sites"]')?.click();
    fixture.detectChanges();

    expect(sidebar(host)).toBeNull();
  });

  it('dismisses an open drawer when the viewport narrows past the wide breakpoint', async () => {
    const media = stubMatchMedia(true);
    const { fixture, host } = await render();
    expect(sidebar(host)).not.toBeNull();

    media.emit(false);
    fixture.detectChanges();

    expect(sidebar(host)).toBeNull();
    expect(sidebarToggle(host).getAttribute('aria-expanded')).toBe('false');
  });
});

describe('AppComponent authenticated user control (issue #391)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('places the signed-in identity and sign-out in the top-right of the header, not in the sidebar', async () => {
    const { fixture, host, msal } = await render({ signedIn: true });

    const header = host.querySelector('header');
    const menu = header?.querySelector<HTMLElement>('app-user-menu');
    expect(menu).not.toBeNull();
    expect(menu?.textContent).toContain('Dana Operator');
    expect(sidebar(host)?.textContent).not.toContain('Dana Operator');
    expect(sidebar(host)?.textContent).not.toContain('Sign out');

    menu?.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();
    const signOut = Array.from(menu?.querySelectorAll('button') ?? []).find((button) =>
      (button.textContent ?? '').includes('Sign out')
    );
    signOut?.click();

    expect(msal.logoutRedirect).toHaveBeenCalledTimes(1);
    expect(msal.logoutRedirect).toHaveBeenCalledWith(
      expect.objectContaining({ account, postLogoutRedirectUri: window.location.origin })
    );
  });

  it('offers sign-in in the same place when no account is active', async () => {
    const { host, msal } = await render({ signedIn: false });

    const header = host.querySelector('header');
    const signIn = Array.from(header?.querySelectorAll('button') ?? []).find((button) =>
      (button.textContent ?? '').includes('Sign in')
    );
    expect(signIn).toBeDefined();
    expect(header?.textContent).not.toContain('Sign out');

    signIn?.click();

    expect(msal.loginRedirect).toHaveBeenCalledTimes(1);
  });
});

describe('AppComponent main content width (issue #454)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('does not cap or center the main content area, so a page can use the full available width', async () => {
    const { host } = await render();

    const main = host.querySelector('main');
    const wrapper = main?.firstElementChild;
    expect(wrapper).not.toBeNull();

    const classList = Array.from(wrapper?.classList ?? []);
    expect(classList).not.toContain('mx-auto');
    expect(classList.some((className) => className.startsWith('max-w-'))).toBe(false);
  });
});

/**
 * The shell is where the business's own time zone enters the application (issue #499). Every
 * instant a page displays is rendered in that zone, so the shell reads the business once sign-in
 * has settled and holds the routed page back until the lookup has answered - either way, so a
 * failed lookup degrades to dates being unavailable rather than to an application that never
 * appears.
 */
describe('AppComponent business context (issue #499)', () => {
  beforeEach(() => stubMatchMedia(true));

  /** The routed page, which is only rendered once the business context is ready. */
  async function navigatedMain(fixture: ComponentFixture<AppComponent>): Promise<string> {
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
  }

  it('reads the signed-in operator business once, and then renders the page', async () => {
    const { fixture, businessLoad } = await render();

    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(await navigatedMain(fixture)).toContain('page body');
  });

  it('asks for no business at all when nobody is signed in, and still renders the page', async () => {
    const { fixture, businessLoad } = await render({ signedIn: false });

    expect(businessLoad).not.toHaveBeenCalled();
    expect(await navigatedMain(fixture)).toContain('page body');
  });

  /**
   * The account-state check issue #529 adds: a person who is not `Member` has no business to look
   * up, and the lookup would only receive the same `403` every other member endpoint already
   * gives that account. `GET /api/business/current` must never be called before the account state
   * is known - this is that ordering.
   */
  it('asks for no business when the signed-in person has no usable membership', async () => {
    const { fixture, businessLoad } = await render({
      accountState: { status: 'known', accountState: { state: 'NoMembership', onboardingEnabled: false, rejectionReason: null } }
    });

    expect(businessLoad).not.toHaveBeenCalled();
    expect(await navigatedMain(fixture)).toContain('page body');
  });

  /**
   * The fail-open rule applied to the shell's own business lookup: the endpoint is the real
   * boundary, so an account state that could not be read must not stop a legitimate member's
   * business context from loading.
   */
  it('still looks up the business when the account state could not be read', async () => {
    const { fixture, businessLoad } = await render({ accountState: { status: 'unavailable' } });

    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(await navigatedMain(fixture)).toContain('page body');
  });

  it('shows a loading state instead of the page while the business lookup is in flight', async () => {
    const pending = new Subject<CurrentBusiness | null>();
    const { fixture } = await render({ business: () => pending.asObservable() });

    const whileLoading = await navigatedMain(fixture);
    expect(whileLoading).toContain('Loading your business');
    expect(whileLoading).not.toContain('page body');

    pending.next({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' });
    fixture.detectChanges();

    const afterLoading = (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
    expect(afterLoading).toContain('page body');
    expect(afterLoading).not.toContain('Loading your business');
  });

  it('reads the business again, as the new account, when the signed-in account changes', async () => {
    const dana = { ...account, homeAccountId: 'dana.tenant' } as AccountInfo;
    const lee = { name: 'Lee Operator', username: 'lee@example.test', homeAccountId: 'lee.tenant' } as unknown as AccountInfo;
    let active: AccountInfo | null = dana;
    const leeBusiness = new Subject<CurrentBusiness | null>();
    const lookups = [
      () => of({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' }),
      () => leeBusiness.asObservable()
    ];
    let lookupCount = 0;
    const { fixture, businessLoad, businessReset, settleInteraction } = await render({
      activeAccount: () => active,
      business: () => lookups[lookupCount++]()
    });
    expect(await navigatedMain(fixture)).toContain('page body');
    const resetsBeforeSwitch = businessReset.mock.calls.length;

    active = lee;
    settleInteraction();

    // The previous account's business context is discarded and read again, and the page waits
    // for the new account's business rather than showing the previous one's zone meanwhile.
    expect(businessReset.mock.calls.length).toBe(resetsBeforeSwitch + 1);
    expect(businessLoad).toHaveBeenCalledTimes(2);
    const whileLoading = (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
    expect(whileLoading).toContain('Loading your business');
    expect(whileLoading).not.toContain('page body');

    leeBusiness.next({ name: 'Vending NY', timeZoneId: 'America/New_York' });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('main')?.textContent).toContain('page body');
  });

  it('does not read the business again when the same account signs in again', async () => {
    const { businessLoad, businessReset, settleInteraction } = await render();
    const resets = businessReset.mock.calls.length;

    settleInteraction();

    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(businessReset.mock.calls.length).toBe(resets);
  });

  it('discards the business context on sign-out and reads it afresh on the next sign-in', async () => {
    let active: AccountInfo | null = account;
    const { businessLoad, businessReset, settleInteraction } = await render({ activeAccount: () => active });
    const resets = businessReset.mock.calls.length;

    active = null;
    settleInteraction();
    expect(businessReset.mock.calls.length).toBe(resets + 1);

    active = account;
    settleInteraction();
    expect(businessLoad).toHaveBeenCalledTimes(2);
  });

  it('retries a failed business lookup before rendering the page, so one blip is not permanent', async () => {
    jest.useFakeTimers();
    try {
      let attempt = 0;
      const { fixture, businessLoad } = await render({
        business: () => (++attempt === 1 ? of(null) : of({ name: 'Vending Co', timeZoneId: 'Australia/Sydney' }))
      });
      expect(businessLoad).toHaveBeenCalledTimes(1);
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('main')?.textContent).toContain('Loading your business');

      jest.advanceTimersByTime(AppComponent.businessLookupRetryDelayMs);
      fixture.detectChanges();

      expect(businessLoad).toHaveBeenCalledTimes(2);
      expect((fixture.nativeElement as HTMLElement).querySelector('main')?.textContent).not.toContain('Loading your business');
    } finally {
      jest.useRealTimers();
    }
  });

  it('renders the page anyway when the business still could not be read after its retries', async () => {
    jest.useFakeTimers();
    try {
      const { fixture, businessLoad } = await render({ business: () => of(null) });

      jest.advanceTimersByTime(
        AppComponent.businessLookupRetryDelayMs * (AppComponent.businessLookupRetries * (AppComponent.businessLookupRetries + 1)) / 2
      );
      fixture.detectChanges();

      expect(businessLoad).toHaveBeenCalledTimes(1 + AppComponent.businessLookupRetries);
      const main = (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
      expect(main).not.toContain('Loading your business');
    } finally {
      jest.useRealTimers();
    }
  });
});

/**
 * A direct Microsoft active-account switch (issue #604). MSAL raises `ACTIVE_ACCOUNT_CHANGED` and
 * leaves `inProgress$` at `None`, so a shell that watched the interaction status alone kept the
 * previous account's name, business and published time zone on screen - the new account-state
 * answer could not repair it, because the previous lookup had already completed.
 *
 * Every test here switches the account through the event only, and never touches `inProgress$`.
 */
describe('AppComponent active-account switch (issue #604)', () => {
  beforeEach(() => stubMatchMedia(true));

  const dana = {
    name: 'Dana Operator',
    username: 'dana@example.test',
    homeAccountId: 'dana.tenant'
  } as unknown as AccountInfo;
  const lee = {
    name: 'Lee Operator',
    username: 'lee@example.test',
    homeAccountId: 'lee.tenant'
  } as unknown as AccountInfo;

  const sydney: CurrentBusiness = { name: 'Vending Co', timeZoneId: 'Australia/Sydney' };
  const london: CurrentBusiness = { name: 'Vending UK', timeZoneId: 'Europe/London' };

  const NO_MEMBERSHIP: AccountStateAnswer = {
    status: 'known',
    accountState: { state: 'NoMembership', onboardingEnabled: false, rejectionReason: null }
  };

  /** Answers A's state at once and holds B's read pending, so the waiting window is a test step. */
  const pendingForLee = (identity: string): AccountStateAnswer | 'pending' =>
    identity === 'lee.tenant' ? 'pending' : MEMBER_ACCOUNT_STATE;

  function mainText(fixture: ComponentFixture<AppComponent>): string {
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
  }

  function headerText(fixture: ComponentFixture<AppComponent>): string {
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector('header')?.textContent ?? '';
  }

  async function onAPage(fixture: ComponentFixture<AppComponent>): Promise<void> {
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();
  }

  it('follows the account the event reports, holding the page until the new context settles', async () => {
    let active: AccountInfo | null = dana;
    const leeBusiness = new Subject<CurrentBusiness | null>();
    const lookups = [() => of(sydney), () => leeBusiness.asObservable()];
    let lookup = 0;
    const { fixture, businessLoad, businessReset, raiseAccountEvent, answerAccountState, accountStateReads } =
      await render({
        activeAccount: () => active,
        accountStateFor: pendingForLee,
        business: () => lookups[lookup++]()
      });
    await onAPage(fixture);
    expect(mainText(fixture)).toContain('page body');
    const resets = businessReset.mock.calls.length;

    active = lee;
    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);

    // The header is the new account's at once, and the previous account's context is discarded.
    expect(headerText(fixture)).toContain('Lee Operator');
    expect(headerText(fixture)).not.toContain('Dana Operator');
    expect(businessReset.mock.calls.length).toBe(resets + 1);

    // The routed-page gate is closed again, and nothing is asked of the business endpoint until
    // the new identity's own account state has answered.
    expect(mainText(fixture)).toContain('Loading your business');
    expect(mainText(fixture)).not.toContain('page body');
    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(accountStateReads).toEqual(['dana.tenant', 'lee.tenant']);

    answerAccountState(MEMBER_ACCOUNT_STATE);

    expect(businessLoad).toHaveBeenCalledTimes(2);
    expect(mainText(fixture)).toContain('Loading your business');

    leeBusiness.next(london);

    expect(mainText(fixture)).toContain('page body');
  });

  it('asks for no business when the new identity’s state says it has no usable membership', async () => {
    let active: AccountInfo | null = dana;
    const { fixture, businessLoad, raiseAccountEvent, answerAccountState } = await render({
      activeAccount: () => active,
      accountStateFor: pendingForLee
    });
    await onAPage(fixture);

    active = lee;
    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
    expect(businessLoad).toHaveBeenCalledTimes(1);

    answerAccountState(NO_MEMBERSHIP);

    expect(businessLoad).toHaveBeenCalledTimes(1);
    // Settled without a business: the page is released rather than waiting for a call that would
    // only be refused.
    expect(mainText(fixture)).toContain('page body');
  });

  it('still looks up the business for the new identity when its account state could not be read', async () => {
    let active: AccountInfo | null = dana;
    const { fixture, businessLoad, raiseAccountEvent, answerAccountState } = await render({
      activeAccount: () => active,
      accountStateFor: pendingForLee
    });
    await onAPage(fixture);

    active = lee;
    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
    answerAccountState({ status: 'unavailable' });

    expect(businessLoad).toHaveBeenCalledTimes(2);
    expect(mainText(fixture)).toContain('page body');
  });

  it('clears the context on a sign-out event, without any protected call', async () => {
    let active: AccountInfo | null = dana;
    const { fixture, businessLoad, businessReset, raiseAccountEvent, accountStateReads } = await render({
      activeAccount: () => active
    });
    await onAPage(fixture);
    const resets = businessReset.mock.calls.length;

    active = null;
    raiseAccountEvent(EventType.LOGOUT_SUCCESS);

    expect(businessReset.mock.calls.length).toBe(resets + 1);
    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(accountStateReads).toEqual(['dana.tenant']);
    expect(headerText(fixture)).not.toContain('Dana Operator');
    // Nobody is signed in, so there is no business context to wait for.
    expect(mainText(fixture)).toContain('page body');
  });

  it('reads one business and keeps the settled context when the same identity is reported again', async () => {
    const { fixture, businessLoad, businessReset, raiseAccountEvent, accountStateReads } = await render();
    await onAPage(fixture);
    const resets = businessReset.mock.calls.length;

    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
    raiseAccountEvent(EventType.LOGIN_SUCCESS);
    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(businessLoad).toHaveBeenCalledTimes(1);
    expect(businessReset.mock.calls.length).toBe(resets);
    expect(accountStateReads).toHaveLength(1);
    expect(mainText(fixture)).toContain('page body');
  });

  it('stops following MSAL once the shell is destroyed', async () => {
    let active: AccountInfo | null = dana;
    const { fixture, businessLoad, businessReset, raiseAccountEvent } = await render({
      activeAccount: () => active
    });
    const resets = businessReset.mock.calls.length;

    fixture.destroy();
    active = lee;
    raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);

    expect(businessReset.mock.calls.length).toBe(resets);
    expect(businessLoad).toHaveBeenCalledTimes(1);
  });

  /**
   * The same switch against the real `BusinessService` and `BusinessTimeZoneService`, because which
   * zone is published is what the reset/generation protection decides - and a stubbed lookup could
   * not show it. Two synthetic business zones, so a wrong one is visible.
   */
  describe('the published business time zone', () => {
    it('publishes the new business’s zone only, dropping the previous one on the switch itself', async () => {
      let active: AccountInfo | null = dana;
      const danaBusiness = new Subject<CurrentBusiness>();
      const leeBusiness = new Subject<CurrentBusiness>();
      const { fixture, timeZone, businessGet, raiseAccountEvent, answerAccountState } = await render({
        activeAccount: () => active,
        accountStateFor: pendingForLee,
        businessHttp: [() => danaBusiness.asObservable(), () => leeBusiness.asObservable()]
      });
      await onAPage(fixture);

      danaBusiness.next(sydney);
      expect(timeZone.timeZoneId).toBe('Australia/Sydney');
      expect(mainText(fixture)).toContain('page body');
      expect(businessGet).toHaveBeenCalledTimes(1);

      active = lee;
      raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);

      // No zone at all while the new business is unknown: a page that renders an instant renders
      // it as unavailable rather than in the previous business's zone.
      expect(timeZone.timeZoneId).toBeNull();
      expect(mainText(fixture)).toContain('Loading your business');
      expect(businessGet).toHaveBeenCalledTimes(1);

      answerAccountState(MEMBER_ACCOUNT_STATE);
      expect(businessGet).toHaveBeenCalledTimes(2);

      leeBusiness.next(london);

      expect(timeZone.timeZoneId).toBe('Europe/London');
      expect(mainText(fixture)).toContain('page body');
    });

    it('lets a superseded response publish nothing and ready nothing when it answers late', async () => {
      let active: AccountInfo | null = dana;
      const danaBusiness = new Subject<CurrentBusiness>();
      const leeBusiness = new Subject<CurrentBusiness>();
      const { fixture, timeZone, businessGet, raiseAccountEvent, answerAccountState } = await render({
        activeAccount: () => active,
        accountStateFor: pendingForLee,
        businessHttp: [() => danaBusiness.asObservable(), () => leeBusiness.asObservable()]
      });
      await onAPage(fixture);
      expect(businessGet).toHaveBeenCalledTimes(1);

      active = lee;
      raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
      answerAccountState(MEMBER_ACCOUNT_STATE);
      expect(businessGet).toHaveBeenCalledTimes(2);

      // The previous account's lookup answers after the switch.
      danaBusiness.next(sydney);

      expect(timeZone.timeZoneId).toBeNull();
      expect(mainText(fixture)).toContain('Loading your business');
      expect(mainText(fixture)).not.toContain('page body');

      leeBusiness.next(london);

      expect(timeZone.timeZoneId).toBe('Europe/London');
      expect(mainText(fixture)).toContain('page body');
    });
  });
});

describe('AppComponent routing (issue #391)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('still renders a routed page reached directly by URL', async () => {
    const { fixture } = await render();

    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('main')?.textContent).toContain('page body');
    expect(sidebar(fixture.nativeElement as HTMLElement)?.querySelector('a[href="/reports/bookkeeping"]')).not.toBeNull();
  });
});
