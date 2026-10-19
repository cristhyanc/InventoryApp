import { HttpClient } from '@angular/common/http';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, AuthenticationResult, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, NEVER, Observable, Subject, of, throwError } from 'rxjs';
import packageJson from '../../package.json';
import { AccessService, MemberAccessState } from './access/access.service';
import { ACCOUNT_STATES, AccountStateAnswer, AccountStateName, AccountStateService } from './access/account-state.service';
import { CAPABILITIES, capabilitySet } from './access/capabilities';
import { signedInAccountMayHaveChanged } from './access/signed-in-account';
import { AppComponent } from './app.component';
import { BusinessTimeZoneService } from './formatting/business-time-zone.service';
import { NavLink, primaryNavigation } from './layout/navigation';
import { ConfigService } from './services/config.service';
import { provideNayaxConnectionStatus } from './provider/provider-availability.spec-helpers';
import { PlatformDiagnosticsAccessService } from './services/platform-diagnostics-access.service';
import { SuperAdminBusinessesService } from './services/super-admin-businesses.service';
import { BusinessService, CurrentBusiness } from './services/business.service';
import { SupportSessionService, SupportSessionState } from './support-session/support-session.service';
import { configureExternalId, unconfigureExternalId } from './auth/external-id.spec-helpers';
import { peekIntendedDestination, rememberIntendedDestination } from './auth/intended-destination';
import { SELECTED_AUTHORITY_KEY } from './auth/selected-authority';
import { SignInService } from './auth/sign-in.service';

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
  /** Every `End session` the blocked explanation asked for (issue #668). */
  supportSessionEnd: jest.Mock;
  /** Every end the shell asked for before signing out (issue #681). */
  supportSessionEndBeforeSignOut: jest.Mock;
  /**
   * Reports a business-context transition starting or settling, the way `SupportSessionService`
   * does around its own dashboard navigation (issue #670). It deliberately does **not** run change
   * detection: the shell has to render the page's removal itself, because a transition can settle
   * before Angular would otherwise look again.
   */
  changeBusinessContext: (changing: boolean) => void;
  /**
   * Reports that the **effective business** changed, as `SupportSessionService` does once per
   * transition after dropping the previous context's cached business (issue #671). It is a separate
   * signal from the settling window above on purpose: two overlapping transitions report one
   * settling window and two effective changes.
   */
  changeEffectiveBusiness: () => void;
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
    /** The support-session state the shell sees; defaults to nothing active (issue #668). */
    supportSession?: Observable<SupportSessionState>;
    /**
     * The capability answer the rendered sidebar builds its menu from; defaults to every
     * capability. `unavailable` is what a signed-out visitor and a known nonmember really get
     * (issue #676), and what a confirmed member's failed capability probe gets too.
     */
    access?: Observable<MemberAccessState>;
    /** The sidebar's separate platform-admin probe; defaults to refused (issue #335). */
    diagnosticsAccess?: Observable<boolean>;
    /**
     * What MSAL's redirect handling reports; defaults to "no interaction to handle". An error here
     * is a cancelled or failed hosted sign-in coming back to `/auth` (issue #711).
     */
    redirect?: Observable<AuthenticationResult | null>;
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
  const supportSessionEnd = jest.fn(() => of(undefined));
  const supportSessionEndBeforeSignOut = jest.fn(() => of(undefined));
  const contextChanging = new BehaviorSubject(false);
  const contextChanged = new Subject<void>();
  const msalService = {
    instance: {
      getActiveAccount: () => activeAccount(),
      getAllAccounts: () => {
        const active = activeAccount();
        return active ? [active] : [];
      },
      setActiveAccount: jest.fn()
    },
    handleRedirectObservable: () => options.redirect ?? of(null),
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
      { provide: PlatformDiagnosticsAccessService, useValue: { granted$: options.diagnosticsAccess ?? of(false) } },
      // The sidebar also reads the pending-business-applications badge (issue #547), tested in
      // sidebar-nav.component.spec.ts; stubbed here only so the real service's HttpClient
      // dependency is not constructed in a shell test that never grants super-admin access.
      { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } },
      // And it reads the shared provider-availability status, which decides what issue #729's menu
      // locks and hides. The shell owns no part of that either: the status read is stubbed as a
      // connected business here and the menu's own behaviour is tested in
      // sidebar-nav.component.spec.ts and navigation.spec.ts.
      provideNayaxConnectionStatus(),
      // The sidebar also renders the capabilities GET /api/me/access reported (issue #528). The
      // shell owns no part of that decision either, so it is stubbed as every capability here and
      // tested in access.service.spec.ts and sidebar-nav.component.spec.ts.
      {
        provide: AccessService,
        useValue: {
          access$:
            options.access ??
            of<MemberAccessState>({
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
      { provide: AccountStateService, useValue: { state$: accountState.state$ } },
      // The shell renders the support-session banner on every page (issue #553) and withholds the
      // routed page while a session cannot be presented by this tab (issue #668). What the state
      // itself is decided from is `SupportSessionService`'s own (support-session.service.spec.ts);
      // the shell's part is only which of the two it renders, so the state is supplied here.
      {
        provide: SupportSessionService,
        useValue: {
          state$: options.supportSession ?? of<SupportSessionState>({ kind: 'inactive' }),
          contextChanging$: contextChanging,
          contextChanged$: contextChanged,
          end: supportSessionEnd,
          endBeforeSignOut: supportSessionEndBeforeSignOut
        }
      }
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
    accountStateReads: accountState.reads,
    supportSessionEnd,
    supportSessionEndBeforeSignOut,
    changeBusinessContext: (changing: boolean) => contextChanging.next(changing),
    changeEffectiveBusiness: () => contextChanged.next()
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

  /** A support session belongs to the administrator signing out, so it ends first (issue #681). */
  it('ends the support session before redirecting to sign out, and only then redirects', async () => {
    const { fixture, host, msal, supportSessionEndBeforeSignOut } = await render({ signedIn: true });
    const ended = new Subject<undefined>();
    supportSessionEndBeforeSignOut.mockReturnValue(ended);

    const menu = host.querySelector('header')?.querySelector<HTMLElement>('app-user-menu');
    menu?.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();
    Array.from(menu?.querySelectorAll('button') ?? [])
      .find((button) => (button.textContent ?? '').includes('Sign out'))
      ?.click();

    expect(supportSessionEndBeforeSignOut).toHaveBeenCalledTimes(1);
    expect(msal.logoutRedirect).not.toHaveBeenCalled();

    ended.next(undefined);
    ended.complete();

    expect(msal.logoutRedirect).toHaveBeenCalledTimes(1);
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
    expect(businessReset.mock.calls).toHaveLength(resets + 1);

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

    expect(businessReset.mock.calls).toHaveLength(resets + 1);
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
    expect(businessReset.mock.calls).toHaveLength(resets);
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

    expect(businessReset.mock.calls).toHaveLength(resets);
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

/**
 * The shell's own part of issue #668: which of the two it renders in the main region while the
 * administrator's support session cannot be presented by this tab. Whether the state is
 * `unresolved` at all is `SupportSessionService`'s decision and is tested there; what matters here
 * is that no routed page - and so no business-scoped request a page would make - is rendered under
 * it, for an administrator who has their own membership and for one who has none.
 */
describe('AppComponent support session this tab cannot present (issue #668)', () => {
  beforeEach(() => stubMatchMedia(true));

  const UNRESOLVED: SupportSessionState = {
    kind: 'unresolved',
    businessName: 'Snack Co',
    expiresAtUtc: '2026-02-01T01:00:00Z'
  };

  const NO_MEMBERSHIP: AccountStateAnswer = {
    status: 'known',
    accountState: { state: 'NoMembership', onboardingEnabled: false, rejectionReason: null }
  };

  async function navigatedMain(fixture: ComponentFixture<AppComponent>): Promise<string> {
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();
    return (fixture.nativeElement as HTMLElement).querySelector('main')?.textContent ?? '';
  }

  it('renders the explanation instead of the routed page for an administrator with their own membership', async () => {
    const { fixture, host } = await render({ supportSession: of(UNRESOLVED) });

    const main = await navigatedMain(fixture);

    expect(main).not.toContain('page body');
    expect(host.querySelector('main [data-testid="support-session-unresolved"]')).not.toBeNull();
    expect(host.textContent).not.toContain('Viewing as');
  });

  it('renders the explanation instead of the routed page for an administrator with no membership', async () => {
    const { fixture, host, businessLoad } = await render({
      supportSession: of(UNRESOLVED),
      accountState: NO_MEMBERSHIP
    });

    const main = await navigatedMain(fixture);

    expect(businessLoad).not.toHaveBeenCalled();
    expect(main).not.toContain('page body');
    expect(host.querySelector('main [data-testid="support-session-unresolved"]')).not.toBeNull();
  });

  it('offers End session from the explanation, which is what returns the tab to ordinary context', async () => {
    const { fixture, host, supportSessionEnd } = await render({ supportSession: of(UNRESOLVED) });
    await navigatedMain(fixture);

    const end = Array.from(host.querySelectorAll('main button')).find(
      (button) => button.textContent?.trim() === 'End session'
    );
    expect(end).toBeDefined();
    (end as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(supportSessionEnd).toHaveBeenCalledTimes(1);
  });

  it('renders the routed page again once the session is over', async () => {
    const state = new BehaviorSubject<SupportSessionState>(UNRESOLVED);
    const { fixture, host } = await render({ supportSession: state });
    expect(await navigatedMain(fixture)).not.toContain('page body');

    state.next({ kind: 'inactive' });

    expect(await navigatedMain(fixture)).toContain('page body');
    expect(host.querySelector('[data-testid="support-session-unresolved"]')).toBeNull();
  });

  it('renders the routed page for an effective support view, which does present the header', async () => {
    const { fixture, host } = await render({
      supportSession: of<SupportSessionState>({
        kind: 'viewing',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    expect(await navigatedMain(fixture)).toContain('page body');
    expect(host.querySelector('[data-testid="support-session-unresolved"]')).toBeNull();
  });
});

/**
 * The shell's own part of issue #670: the routed page is withheld - and so destroyed, and so built
 * again - for exactly the window `SupportSessionService` reports a business-context transition in.
 * Which transitions there are, and what else each of them does, is the service's own decision
 * (`support-session.service.spec.ts`), and what the operator then sees on a real Dashboard is
 * `support-session-context-transition.spec.ts`.
 */
describe('AppComponent business-context transition (issue #670)', () => {
  beforeEach(() => stubMatchMedia(true));

  function main(host: HTMLElement): string {
    return host.querySelector('main')?.textContent ?? '';
  }

  async function onAPage(rendered: Rendered): Promise<Element | null> {
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    rendered.fixture.detectChanges();
    expect(main(rendered.host)).toContain('page body');
    return rendered.host.querySelector('main router-outlet');
  }

  it('takes the routed page off screen as soon as a transition starts', async () => {
    const rendered = await render();
    await onAPage(rendered);

    // No `detectChanges` here on purpose: a transition can settle in the same turn it started in -
    // a tab already on the dashboard has nothing to navigate - so a shell that waited for Angular
    // to look again would leave the previous business's page exactly where it was.
    rendered.changeBusinessContext(true);

    expect(main(rendered.host)).not.toContain('page body');
    expect(rendered.host.querySelector('main router-outlet')).toBeNull();
    expect(main(rendered.host)).toContain('Loading your business…');
  });

  /** The same turn, which is the case a flag alone cannot survive. */
  it('builds a new page when a transition starts and settles before Angular looks again', async () => {
    const rendered = await render();
    const before = await onAPage(rendered);

    rendered.changeBusinessContext(true);
    rendered.changeBusinessContext(false);
    rendered.fixture.detectChanges();

    const after = rendered.host.querySelector('main router-outlet');
    expect(main(rendered.host)).toContain('page body');
    expect(after).not.toBeNull();
    // A different outlet is a different page instance: the figures the previous business's page
    // held are gone with it, rather than being reused for the business now in view.
    expect(after).not.toBe(before);
  });

  it('renders no page for a tab that cannot present the session, whatever the transition reports', async () => {
    const rendered = await render({
      supportSession: of<SupportSessionState>({
        kind: 'unresolved',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    rendered.changeBusinessContext(true);
    rendered.changeBusinessContext(false);
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    rendered.fixture.detectChanges();

    expect(main(rendered.host)).not.toContain('page body');
    expect(rendered.host.querySelector('main [data-testid="support-session-unresolved"]')).not.toBeNull();
  });
});

/**
 * The shell reads the **effective** business whenever the business it resolves changes under the
 * same signed-in identity (issue #671), which the once-per-account lookup of issues #499 and #604
 * did not cover: a support start, end or expiry dropped the published zone without reading the new
 * one, and an accepted invite or an approved application turned the identity already signed in into
 * a `Member` long after that lookup had settled with no business at all.
 *
 * What is asserted here is the shell's own decisions - when it reads again, whether the page waits
 * for the answer, and what it says when there is none. Which zone actually reaches the page over the
 * real `BusinessService`, the real session and the production interceptor chain is
 * `support-session/support-session-business-time-zone.spec.ts`.
 */
describe('AppComponent effective business context (issue #671)', () => {
  beforeEach(() => stubMatchMedia(true));

  const sydney: CurrentBusiness = { name: 'Vending Co', timeZoneId: 'Australia/Sydney' };
  const newYork: CurrentBusiness = { name: 'Snack Co', timeZoneId: 'America/New_York' };

  const NO_MEMBERSHIP: AccountStateAnswer = {
    status: 'known',
    accountState: { state: 'NoMembership', onboardingEnabled: false, rejectionReason: null }
  };
  const APPLICATION_PENDING: AccountStateAnswer = {
    status: 'known',
    accountState: { state: 'ApplicationPending', onboardingEnabled: true, rejectionReason: null }
  };

  /** The sum of the growing delays between the lookup's retries. */
  const allRetryDelaysMs =
    (AppComponent.businessLookupRetryDelayMs *
      (AppComponent.businessLookupRetries * (AppComponent.businessLookupRetries + 1))) /
    2;

  function main(host: HTMLElement): string {
    return host.querySelector('main')?.textContent ?? '';
  }

  function unavailableNotice(host: HTMLElement): HTMLElement | null {
    return host.querySelector<HTMLElement>('main [data-testid="business-context-unavailable"]');
  }

  function tryAgain(host: HTMLElement): HTMLButtonElement {
    const button = Array.from(unavailableNotice(host)?.querySelectorAll('button') ?? []).find(
      (candidate) => candidate.textContent?.trim() === 'Try again'
    );
    expect(button).toBeDefined();
    return button as HTMLButtonElement;
  }

  async function onAPage(rendered: Rendered): Promise<Element | null> {
    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    rendered.fixture.detectChanges();
    expect(main(rendered.host)).toContain('page body');
    return rendered.host.querySelector('main router-outlet');
  }

  it('reads the business again when the effective business changes, holding the page until it answers', async () => {
    const changed = new Subject<CurrentBusiness | null>();
    let lookup = 0;
    const rendered = await render({ business: () => (lookup++ === 0 ? of(sydney) : changed.asObservable()) });
    await onAPage(rendered);

    rendered.changeEffectiveBusiness();
    rendered.fixture.detectChanges();

    expect(rendered.businessLoad).toHaveBeenCalledTimes(2);
    expect(main(rendered.host)).toContain('Loading your business');
    expect(main(rendered.host)).not.toContain('page body');

    changed.next(newYork);
    rendered.fixture.detectChanges();

    expect(main(rendered.host)).toContain('page body');
  });

  /** The case the settling window cannot report: one `true` covers two effective changes. */
  it('reads the business for every effective change, including two inside one settling window', async () => {
    const rendered = await render();
    await onAPage(rendered);

    rendered.changeBusinessContext(true);
    rendered.changeEffectiveBusiness();
    rendered.changeEffectiveBusiness();
    rendered.changeBusinessContext(false);
    rendered.fixture.detectChanges();

    expect(rendered.businessLoad).toHaveBeenCalledTimes(3);
    expect(main(rendered.host)).toContain('page body');
  });

  it.each([
    ['an accepted invite', NO_MEMBERSHIP],
    ['an approved application', APPLICATION_PENDING]
  ])('reads the business when %s makes the same identity a Member, leaving the page alone', async (_case, before) => {
    const joined = new Subject<CurrentBusiness | null>();
    const rendered = await render({ accountState: before, business: () => joined.asObservable() });
    const page = await onAPage(rendered);
    expect(rendered.businessLoad).not.toHaveBeenCalled();

    rendered.answerAccountState(MEMBER_ACCOUNT_STATE);
    rendered.fixture.detectChanges();

    expect(rendered.businessLoad).toHaveBeenCalledTimes(1);
    // The page is the one the acceptance or approval is navigating away from, and it is still
    // performing that navigation: taking it off screen would tear it down with it.
    expect(main(rendered.host)).toContain('page body');
    expect(rendered.host.querySelector('main router-outlet')).toBe(page);

    joined.next(sydney);
    rendered.fixture.detectChanges();

    expect(main(rendered.host)).toContain('page body');
    expect(unavailableNotice(rendered.host)).toBeNull();
  });

  it('publishes the business’s own zone once a nonmember account becomes a Member', async () => {
    const rendered = await render({ accountState: NO_MEMBERSHIP, businessHttp: [() => of(newYork)] });
    expect(rendered.businessGet).not.toHaveBeenCalled();
    expect(rendered.timeZone.timeZoneId).toBeNull();

    rendered.answerAccountState(MEMBER_ACCOUNT_STATE);

    expect(rendered.businessGet).toHaveBeenCalledTimes(1);
    expect(rendered.timeZone.timeZoneId).toBe('America/New_York');
  });

  it('reads no business for an account-state answer that is still not a Member', async () => {
    const rendered = await render({ accountState: NO_MEMBERSHIP });

    rendered.answerAccountState(APPLICATION_PENDING);
    rendered.answerAccountState({ status: 'unavailable' });
    rendered.fixture.detectChanges();

    expect(rendered.businessLoad).not.toHaveBeenCalled();
    expect(unavailableNotice(rendered.host)).toBeNull();
  });

  it('reads one business when a Member answer is published again for the context that read it', async () => {
    const rendered = await render();
    expect(rendered.businessLoad).toHaveBeenCalledTimes(1);

    rendered.answerAccountState(MEMBER_ACCOUNT_STATE);

    expect(rendered.businessLoad).toHaveBeenCalledTimes(1);
  });

  it('says the business could not be read, rather than leaving every instant blank in silence', async () => {
    jest.useFakeTimers();
    try {
      const rendered = await render({ business: () => of(null) });

      jest.advanceTimersByTime(allRetryDelaysMs);
      rendered.fixture.detectChanges();

      const notice = unavailableNotice(rendered.host);
      expect(notice).not.toBeNull();
      expect(notice?.getAttribute('role')).toBe('alert');
      expect(notice?.textContent).toContain('dates and times are unavailable');
      // The application is still rendered rather than waiting forever (issue #499), and no zone was
      // published, so instants render as unavailable and never in the browser's own zone.
      expect(main(rendered.host)).not.toContain('Loading your business');
      expect(rendered.host.querySelector('main router-outlet')).not.toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('reads the business again from that retry, and stops saying so once it answers', async () => {
    jest.useFakeTimers();
    try {
      const retried = new Subject<CurrentBusiness | null>();
      let attempt = 0;
      const rendered = await render({
        business: () => (++attempt <= 1 + AppComponent.businessLookupRetries ? of(null) : retried.asObservable())
      });
      jest.advanceTimersByTime(allRetryDelaysMs);
      rendered.fixture.detectChanges();

      tryAgain(rendered.host).click();
      rendered.fixture.detectChanges();

      expect(rendered.businessLoad).toHaveBeenCalledTimes(2 + AppComponent.businessLookupRetries);
      expect(unavailableNotice(rendered.host)).toBeNull();
      expect(main(rendered.host)).toContain('Loading your business');

      retried.next(sydney);
      rendered.fixture.detectChanges();

      expect(unavailableNotice(rendered.host)).toBeNull();
      expect(main(rendered.host)).not.toContain('Loading your business');
    } finally {
      jest.useRealTimers();
    }
  });

  it('says nothing about an account that simply has no business to look up', async () => {
    const rendered = await render({ accountState: NO_MEMBERSHIP });

    expect(rendered.businessLoad).not.toHaveBeenCalled();
    expect(unavailableNotice(rendered.host)).toBeNull();
  });

  it('renders no page, and no explanation, while this tab cannot present the session', async () => {
    const rendered = await render({
      business: () => of(null),
      supportSession: of<SupportSessionState>({
        kind: 'unresolved',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    rendered.fixture.detectChanges();

    expect(unavailableNotice(rendered.host)).toBeNull();
    expect(rendered.host.querySelector('main [data-testid="support-session-unresolved"]')).not.toBeNull();
  });
});

/**
 * Which authenticated account states the shell offers the ordinary business navigation for
 * (issue #676).
 *
 * The real sidebar is rendered, because what an operator can reach is the rendered menu and
 * nothing else; only the two access answers it asks for are supplied. `unavailable` is what
 * `GET /api/me/access` really answers for a signed-out visitor and for a known nonmember - the
 * state the menu used to read as "offer every destination" - so these states are rendered with it,
 * and the confirmed member whose probe failed is rendered with it too. The two cases must not
 * behave the same way, which is the whole defect.
 */
describe('AppComponent business navigation by account state (issue #676)', () => {
  beforeEach(() => stubMatchMedia(true));

  /** Every published state that is not `Member`, so a state added later is covered here too. */
  const NONMEMBER_STATES = ACCOUNT_STATES.filter((state) => state !== 'Member');

  const UNAVAILABLE_ACCESS = of<MemberAccessState>({ status: 'unavailable' });

  const dana = { name: 'Dana', homeAccountId: 'dana-home-id' } as unknown as AccountInfo;
  const lee = { name: 'Lee', homeAccountId: 'lee-home-id' } as unknown as AccountInfo;

  function known(state: AccountStateName): AccountStateAnswer {
    return { status: 'known', accountState: { state, onboardingEnabled: false, rejectionReason: null } };
  }

  /** Every ordinary business destination the rendered menu offers; `Super Admin` is not one. */
  function businessDestinations(host: HTMLElement): string[] {
    const nav = host.querySelector('nav[aria-label="Primary"]');
    return Array.from(nav?.querySelectorAll('a') ?? [])
      .map((anchor) => anchor.getAttribute('href') ?? '')
      .filter((href) => !href.startsWith('/super-admin'));
  }

  /** The top-level destinations a member is offered before any group is opened. */
  const memberDestinations = primaryNavigation
    .filter((item): item is NavLink => item.kind === 'link')
    .map((item) => item.route);

  it('offers the business navigation to a confirmed Member', async () => {
    const { host } = await render({ accountState: MEMBER_ACCOUNT_STATE });

    expect(businessDestinations(host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  it('withholds it from a signed-out visitor', async () => {
    const { host } = await render({ signedIn: false, access: UNAVAILABLE_ACCESS });

    expect(businessDestinations(host)).toEqual([]);
  });

  it('withholds it while the account state has not been answered yet', async () => {
    const rendered = await render({ accountStateFor: () => 'pending' });

    expect(businessDestinations(rendered.host)).toEqual([]);

    rendered.answerAccountState(MEMBER_ACCOUNT_STATE);
    rendered.fixture.detectChanges();

    expect(businessDestinations(rendered.host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  it.each(NONMEMBER_STATES)('withholds it from a known %s account', async (state) => {
    const { host } = await render({ accountState: known(state), access: UNAVAILABLE_ACCESS });

    expect(businessDestinations(host)).toEqual([]);
  });

  /**
   * The fallback issue #528 documents, kept exactly as it was: the API refuses what the role may
   * not do regardless, so a member whose capability probe failed keeps every destination rather
   * than being stranded with an empty menu. Only the account state withholds the menu now.
   */
  it('keeps every destination for a confirmed Member whose capability probe failed', async () => {
    const { host } = await render({ accountState: MEMBER_ACCOUNT_STATE, access: UNAVAILABLE_ACCESS });

    expect(businessDestinations(host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  it('withholds it from the moment the signed-in identity changes until the new state answers', async () => {
    let active: AccountInfo | null = dana;
    const rendered = await render({
      activeAccount: () => active,
      accountStateFor: (identity) => (identity === accountKeyOf(dana) ? MEMBER_ACCOUNT_STATE : 'pending')
    });
    expect(businessDestinations(rendered.host)).toEqual(expect.arrayContaining(memberDestinations));

    active = lee;
    rendered.raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
    rendered.fixture.detectChanges();

    expect(businessDestinations(rendered.host)).toEqual([]);

    rendered.answerAccountState(known('NoMembership'));
    rendered.fixture.detectChanges();

    expect(businessDestinations(rendered.host)).toEqual([]);
  });

  it('offers it again once the new identity answers Member', async () => {
    let active: AccountInfo | null = dana;
    const rendered = await render({
      activeAccount: () => active,
      accountStateFor: (identity) => (identity === accountKeyOf(dana) ? known('NoMembership') : 'pending')
    });
    expect(businessDestinations(rendered.host)).toEqual([]);

    active = lee;
    rendered.raiseAccountEvent(EventType.ACTIVE_ACCOUNT_CHANGED);
    rendered.answerAccountState(MEMBER_ACCOUNT_STATE);
    rendered.fixture.detectChanges();

    expect(businessDestinations(rendered.host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  /**
   * The account state is read from `GET /api/me/account-state`, and a read that could not be
   * answered stays the documented fail-open case (issue #529): `accountStateGuard` lets the
   * navigation through, and the menu keeps the destinations whose endpoints remain the boundary.
   */
  it('offers it when the account state itself could not be read', async () => {
    const { host } = await render({ accountState: { status: 'unavailable' } });

    expect(businessDestinations(host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  it('keeps the separately authorized Super Admin group for an administrator with no membership', async () => {
    const { host } = await render({
      accountState: known('NoMembership'),
      access: UNAVAILABLE_ACCESS,
      diagnosticsAccess: of(true)
    });

    expect(businessDestinations(host)).toEqual([]);
    expect(host.querySelector('nav[aria-label="Primary"]')?.textContent).toContain('Super Admin');
  });

  it('offers the business navigation of an effective support view to an administrator with no membership', async () => {
    const { host } = await render({
      accountState: known('NoMembership'),
      supportSession: of<SupportSessionState>({
        kind: 'viewing',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    expect(businessDestinations(host)).toEqual(expect.arrayContaining(memberDestinations));
  });

  /**
   * A tab that cannot present the session resolves the administrator's **own** membership
   * (issue #668), so it is their own account state that decides the menu - and the routed page
   * stays withheld whatever the menu offers.
   */
  it('withholds it in a tab that cannot present the session, whose own account holds no membership', async () => {
    const { fixture, host } = await render({
      accountState: known('NoMembership'),
      access: UNAVAILABLE_ACCESS,
      supportSession: of<SupportSessionState>({
        kind: 'unresolved',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    await TestBed.inject(Router).navigateByUrl('/reports/bookkeeping');
    fixture.detectChanges();

    expect(businessDestinations(host)).toEqual([]);
    expect(host.querySelector('main [data-testid="support-session-unresolved"]')).not.toBeNull();
    expect(host.querySelector('main')?.textContent).not.toContain('page body');
  });
});

/**
 * The shell's part in the authority choice (issue #711): the header's **Sign in**, forgetting the
 * choice on sign-out, and what happens when the hosted sign-in comes back refused.
 */
describe('AppComponent sign-in choice (issue #711)', () => {
  beforeEach(() => {
    stubMatchMedia(true);
    localStorage.clear();
    sessionStorage.clear();
    unconfigureExternalId();
    // A refused redirect is still logged, exactly as before; the tests below assert what the
    // person is shown, and the log would only make the run noisy.
    jest.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  afterEach(() => {
    unconfigureExternalId();
    jest.restoreAllMocks();
  });

  it('redirects straight to the workforce authority with no second authority configured', async () => {
    const { fixture, msal } = await render({ signedIn: false });

    fixture.componentInstance.login();
    await fixture.whenStable();

    expect(msal.loginRedirect).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(Router).url).not.toBe('/sign-in');
  });

  it('opens the sign-in page instead, once a second authority is configured', async () => {
    configureExternalId();
    const { fixture, msal } = await render({ signedIn: false });

    fixture.componentInstance.login();
    await fixture.whenStable();

    expect(msal.loginRedirect).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/sign-in');
  });

  /**
   * A sign-out ends one authority's session; the next person at this browser is asked again rather
   * than sent silently back to the authority the previous one used (design § 9.3).
   */
  it('forgets the chosen authority when signing out', async () => {
    configureExternalId();
    localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');
    const { fixture, msal } = await render();

    fixture.componentInstance.logout();

    expect(localStorage.getItem(SELECTED_AUTHORITY_KEY)).toBeNull();
    expect(msal.logoutRedirect).toHaveBeenCalledTimes(1);
  });

  /**
   * A cancelled hosted journey comes back to `/auth`, whose only message is "Signing you in...".
   * The shell takes the person to the sign-in page with the reason, and starts no second redirect:
   * retrying a refusing authority automatically is how a callback page becomes a loop.
   */
  it('reports a refused redirect on the sign-in page and starts no second redirect', async () => {
    configureExternalId();
    const { fixture, msal } = await render({
      signedIn: false,
      redirect: throwError(() => ({ errorCode: 'access_denied' }))
    });
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/sign-in');
    expect(TestBed.inject(SignInService).take()).toBe('cancelled');
    expect(msal.loginRedirect).not.toHaveBeenCalled();
  });

  it('tells a broken redirect apart from a cancelled one', async () => {
    const { fixture } = await render({
      signedIn: false,
      redirect: throwError(() => ({ errorCode: 'endpoints_resolution_error' }))
    });
    await fixture.whenStable();

    expect(TestBed.inject(SignInService).take()).toBe('failed');
  });

  /**
   * The page the visitor asked for has to survive the cancellation too, or the retry the sign-in
   * page offers quietly lands them on the dashboard instead of where they were going. An invite
   * acceptance page is the case that matters most: being sent to the dashboard after cancelling
   * once leaves the invite unaccepted with nothing on screen saying so.
   */
  it('keeps the page the visitor asked for when a cancelled journey comes back', async () => {
    configureExternalId();
    rememberIntendedDestination('/invite');
    const { fixture } = await render({
      signedIn: false,
      redirect: throwError(() => ({ errorCode: 'access_denied' }))
    });
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/sign-in');
    expect(peekIntendedDestination()).toBe('/invite');
  });

  /** And is released by the one callback that completes, which is MSAL's own cue to navigate
   * there: holding it any longer would return a later sign-in to a stale page. */
  it('releases the destination once the hosted sign-in completes', async () => {
    configureExternalId();
    rememberIntendedDestination('/reports/daily');
    const { fixture } = await render({
      redirect: of({ account } as AuthenticationResult)
    });
    await fixture.whenStable();

    expect(peekIntendedDestination()).toBeNull();
  });
});

describe('AppComponent version footer (issue #648)', () => {
  beforeEach(() => stubMatchMedia(true));

  it('shows the frontend package version in an application-wide footer', async () => {
    const { host } = await render();

    const footer = host.querySelector('footer');
    expect(footer).not.toBeNull();
    expect(footer?.textContent?.trim()).toBe(`Version ${packageJson.version}`);
  });
});
