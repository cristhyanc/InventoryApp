import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, of } from 'rxjs';
import { AppComponent } from './app.component';
import { PlatformDiagnosticsAccessService } from './services/platform-diagnostics-access.service';

@Component({ standalone: true, template: 'page body' })
class BlankPageComponent {}

const account = { name: 'Dana Operator', username: 'dana@example.test' } as unknown as AccountInfo;

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
}

async function render(options: { signedIn?: boolean } = {}): Promise<Rendered> {
  const signedIn = options.signedIn ?? true;
  const loginRedirect = jest.fn();
  const logoutRedirect = jest.fn();
  const msalService = {
    instance: {
      getActiveAccount: () => (signedIn ? account : null),
      getAllAccounts: () => (signedIn ? [account] : []),
      setActiveAccount: jest.fn()
    },
    handleRedirectObservable: () => of(null),
    loginRedirect,
    logoutRedirect
  };

  await TestBed.configureTestingModule({
    imports: [AppComponent],
    providers: [
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      { provide: MsalService, useValue: msalService },
      { provide: MsalBroadcastService, useValue: { inProgress$: new BehaviorSubject(InteractionStatus.None) } },
      // The shell renders the sidebar, which asks the diagnostics API whether to offer the
      // super-admin link (issue #335). The shell itself owns no part of that decision, so the
      // probe is stubbed as refused here and tested where it belongs.
      { provide: PlatformDiagnosticsAccessService, useValue: { isGranted: () => of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(AppComponent);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, msal: { loginRedirect, logoutRedirect } };
}

function sidebarToggle(host: HTMLElement): HTMLButtonElement {
  const toggle = host.querySelector<HTMLButtonElement>('header button[aria-controls="primary-navigation"]');
  expect(toggle).not.toBeNull();
  return toggle as HTMLButtonElement;
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
