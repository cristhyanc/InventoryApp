import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, EnvironmentProviders, Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, NEVER, Observable, Subject, of, throwError } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../access/access.service';
import { BusinessNavigationVisibility } from '../access/business-navigation';
import { CAPABILITIES, Capability, capabilitySet } from '../access/capabilities';
import { OptionVisibility } from '../access/option-visibility';
import { FORBIDDEN_INTERCEPTORS } from '../access/forbidden-interceptors';
import { apiConfig, queuedReads, readingHttp, signedInIdentity } from '../access/identity-cache.spec-helpers';
import { ICON_PATHS, OUTLINED_ICON_SHAPES } from '../components/shared/icon-paths';
import { ProviderAvailabilityService } from '../provider/provider-availability.service';
import {
  SpecConnectionStatus,
  nayaxConnectionStatusDouble,
  provideNayaxConnectionStatus
} from '../provider/provider-availability.spec-helpers';
import { PROVIDER_LOCKED_PATH } from '../provider/provider-lock';
import { requireProvider } from '../provider/provider-lock.guard';
import { ConfigService } from '../services/config.service';
import {
  NayaxConnectionCheckLine,
  NayaxConnectionService,
  NayaxConnectionState,
  NayaxConnectionStatusName
} from '../services/nayax-connection.service';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { PlatformDiagnosticsAccess, PlatformDiagnosticsService } from '../services/platform-diagnostics.service';
import { SuperAdminBusinessesService } from '../services/super-admin-businesses.service';
import { ToastService } from '../services/toast.service';
import { PRIMARY_NAVIGATION_ID, SidebarNavComponent } from './sidebar-nav.component';
import { NavGroup, navLinks, primaryNavigation } from './navigation';

@Component({ standalone: true, template: '' })
class BlankPageComponent {}

function granted(
  capabilities: readonly Capability[],
  optionVisibility?: OptionVisibility
): MemberAccessState {
  const access: MemberAccess = {
    role: 'Test',
    capabilities: capabilitySet([...capabilities]),
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney',
    optionVisibility
  };
  return { status: 'granted', access };
}

/** Every capability, which is what an Owner holds and what the shell tests assume by default. */
const EVERY_CAPABILITY = granted(CAPABILITIES);

interface Rendered {
  fixture: ComponentFixture<SidebarNavComponent>;
  component: SidebarNavComponent;
  host: HTMLElement;
}

/**
 * Both access answers are stubbed, because the sidebar must never be the thing that decides
 * either: it renders the capabilities `GET /api/me/access` reported (issue #528) and the
 * diagnostics API's separate answer about platform-admin access (issue #335).
 */
async function render(
  options: {
    expanded?: boolean;
    drawer?: boolean;
    url?: string;
    navId?: string;
    diagnosticsAccess?: Observable<boolean>;
    access?: Observable<MemberAccessState>;
    /** The real `AccessService`, for the tests that are about what it publishes when (issue #603). */
    accessService?: AccessService;
    pendingCount?: () => Observable<number>;
    businessesChanged$?: Observable<void>;
    /**
     * Whether the shell offers the ordinary business destinations at all (issue #676). It defaults
     * to `offered`, the signed-in member every test here but that issue's own is about; the
     * decision itself is the shell's (`app.component.spec.ts`) and the rule's
     * (`business-navigation.spec.ts`).
     */
    businessNavigation?: BusinessNavigationVisibility;
    /**
     * Whether the Super Admin is previewing a business role (issue #721). Like the two answers
     * above it is the shell's, and the sidebar only renders it.
     */
    rolePreviewActive?: boolean;
    /**
     * The published Nayax connection status the shared availability signal reads (issue #728), which
     * decides what issue #729's menu locks and hides. It defaults to `Ready`, the connected business
     * every test here but that issue's own is about, so those tests see the menu unchanged.
     */
    providerStatus?: SpecConnectionStatus;
    /**
     * Permission evidence to seed the shared signal with before the sidebar renders, as a completed
     * re-test's check lines - the only way a session learns that Nayax refused one read
     * (issue #728). Used for the `PendingPermissions` case of issue #729.
     */
    providerChecks?: readonly NayaxConnectionCheckLine[];
  } = {}
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [SidebarNavComponent],
    providers: [
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      provideNayaxConnectionStatus(options.providerStatus ?? 'Ready'),
      {
        provide: AccessService,
        useValue: options.accessService ?? { access$: options.access ?? of(EVERY_CAPABILITY) }
      },
      {
        provide: PlatformDiagnosticsAccessService,
        useValue: { granted$: options.diagnosticsAccess ?? of(false) }
      },
      {
        provide: SuperAdminBusinessesService,
        useValue: {
          pendingCount: jest.fn(options.pendingCount ?? (() => of(0))),
          changed$: options.businessesChanged$ ?? NEVER
        }
      }
    ]
  }).compileComponents();

  if (options.url) {
    await TestBed.inject(Router).navigateByUrl(options.url);
  }

  if (options.providerChecks !== undefined) {
    // Through the signal's own `accept`, so the per-feature answers are derived by issue #728's
    // code from a completed run's check lines rather than written out by this spec.
    TestBed.inject(ProviderAvailabilityService).accept(
      { status: (options.providerStatus ?? 'PendingPermissions') as NayaxConnectionStatusName, machineChecksPending: false },
      options.providerChecks
    );
  }

  const fixture = TestBed.createComponent(SidebarNavComponent);
  fixture.componentRef.setInput('expanded', options.expanded ?? true);
  fixture.componentRef.setInput('drawer', options.drawer ?? false);
  fixture.componentRef.setInput('businessNavigation', options.businessNavigation ?? 'offered');
  fixture.componentRef.setInput('rolePreviewActive', options.rolePreviewActive ?? false);
  if (options.navId !== undefined) {
    fixture.componentRef.setInput('navId', options.navId);
  }
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function groupToggle(host: HTMLElement, label: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => (button.textContent ?? '').includes(label));
  expect(match).toBeDefined();
  return match as HTMLButtonElement;
}

function link(host: HTMLElement, route: string): HTMLAnchorElement | null {
  return host.querySelector<HTMLAnchorElement>(`a[href="${route}"]`);
}

function navGroup(label: string): NavGroup {
  return primaryNavigation.find((entry) => entry.label === label) as NavGroup;
}

const adminChildRoutes = navLinks([navGroup('Admin')]).map((child) => child.route);

describe('SidebarNavComponent structure (issue #391)', () => {
  it('renders one primary navigation landmark with every top-level entry', async () => {
    const { host } = await render();

    const nav = host.querySelector('nav[aria-label="Primary"]');
    expect(nav).not.toBeNull();
    for (const entry of primaryNavigation) {
      expect(nav?.textContent).toContain(entry.label);
    }
  });

  it('renders the top-level direct links as links, not as group toggles', async () => {
    const { host } = await render();

    expect(link(host, '/')).not.toBeNull();
    expect(link(host, '/pick-list')).not.toBeNull();
    expect(link(host, '/machines')).not.toBeNull();
    expect(link(host, '/sites')).not.toBeNull();
    expect(link(host, '/expenses')).not.toBeNull();
    expect(link(host, '/plan')).not.toBeNull();
  });

  it('keeps every group collapsed until its heading is used, and exposes aria-expanded on it', async () => {
    const { host } = await render();

    for (const label of ['Inventory', 'Purchases', 'Reports', 'Admin']) {
      expect(groupToggle(host, label).getAttribute('aria-expanded')).toBe('false');
    }

    expect(link(host, '/reports/gst')).toBeNull();
    expect(link(host, '/admin/costing-repair')).toBeNull();
  });

  it('expands and collapses a group from its heading, which is a button and not a destination', async () => {
    const { fixture, host } = await render();
    const toggle = groupToggle(host, 'Admin');
    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle.getAttribute('type')).toBe('button');

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector(`#${toggle.getAttribute('aria-controls')}`)).not.toBeNull();
    for (const route of adminChildRoutes) {
      expect(link(host, route)).not.toBeNull();
    }

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(link(host, '/admin/costing-repair')).toBeNull();
  });

  it('renders a link for every declared destination once its group is open', async () => {
    const { fixture, host } = await render();
    for (const label of ['Inventory', 'Purchases', 'Reports', 'Admin']) {
      groupToggle(host, label).click();
      fixture.detectChanges();
    }

    for (const declared of navLinks(primaryNavigation)) {
      expect(link(host, declared.route)).not.toBeNull();
    }
  });
});

describe('SidebarNavComponent active route (issue #391)', () => {
  it('marks the active page with aria-current and opens its parent group', async () => {
    const { host } = await render({ url: '/reports/gst' });

    expect(link(host, '/reports/gst')?.getAttribute('aria-current')).toBe('page');
    expect(groupToggle(host, 'Reports').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/reports/bookkeeping')?.getAttribute('aria-current')).toBeNull();
  });

  it('keeps the parent group heading active while one of its pages is open', async () => {
    const { component, host } = await render({ url: '/admin/avco-transition' });

    expect(component.isGroupActive(navGroup('Admin'))).toBe(true);
    expect(component.isGroupActive(navGroup('Reports'))).toBe(false);
    expect(groupToggle(host, 'Admin').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/admin/avco-transition')?.getAttribute('aria-current')).toBe('page');
  });

  it('gives the active item the Restockroo teal styling hook and its parent heading a visible active hook (issue #972)', async () => {
    const { host } = await render({ url: '/admin/avco-transition' });

    expect(link(host, '/admin/avco-transition')?.classList.contains('bg-rr-teal')).toBe(true);
    expect(link(host, '/admin/avco-transition')?.classList.contains('text-white')).toBe(true);
    expect(link(host, '/admin/historical-cost-recovery')?.classList.contains('bg-rr-teal')).toBe(false);
    expect(groupToggle(host, 'Admin').classList.contains('bg-rr-surface')).toBe(true);
    expect(groupToggle(host, 'Reports').classList.contains('bg-rr-surface')).toBe(false);
  });

  it('marks a top-level link active from a detail URL without activating any group', async () => {
    const { host } = await render({ url: '/machines/7' });

    expect(link(host, '/machines')?.getAttribute('aria-current')).toBe('page');
    expect(groupToggle(host, 'Inventory').getAttribute('aria-expanded')).toBe('false');
    expect(link(host, '/machines')?.classList.contains('bg-rr-teal')).toBe(true);
    expect(link(host, '/')?.classList.contains('bg-rr-teal')).toBe(false);
  });

  it('follows navigation that happens after the sidebar is rendered', async () => {
    const { fixture, host } = await render({ url: '/' });
    expect(link(host, '/')?.getAttribute('aria-current')).toBe('page');

    await TestBed.inject(Router).navigateByUrl('/purchases/orders');
    fixture.detectChanges();

    expect(link(host, '/')?.getAttribute('aria-current')).toBeNull();
    expect(link(host, '/purchases/orders')?.getAttribute('aria-current')).toBe('page');
    expect(link(host, '/purchases')?.getAttribute('aria-current')).toBeNull();
  });
});

describe('SidebarNavComponent collapsed state (issue #391)', () => {
  it('hides the labels visually but keeps every accessible name when collapsed', async () => {
    const { host } = await render({ expanded: false });

    const dashboard = link(host, '/');
    expect(dashboard?.textContent).toContain('Dashboard');
    expect(dashboard?.querySelector('.sr-only')?.textContent?.trim()).toBe('Dashboard');
    expect(dashboard?.getAttribute('title')).toBe('Dashboard');
  });

  it('shows the labels when expanded', async () => {
    const { host } = await render({ expanded: true });

    expect(link(host, '/')?.querySelector('.sr-only')).toBeNull();
    expect(link(host, '/')?.getAttribute('title')).toBeNull();
  });

  it('shows no submenu while collapsed and asks the shell to expand instead', async () => {
    const { fixture, component, host } = await render({ expanded: false });
    const expandRequested = jest.fn();
    component.expandRequested.subscribe(expandRequested);
    const toggle = groupToggle(host, 'Reports');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    toggle.click();
    fixture.detectChanges();

    expect(expandRequested).toHaveBeenCalledTimes(1);
    expect(link(host, '/reports/gst')).toBeNull();

    fixture.componentRef.setInput('expanded', true);
    fixture.detectChanges();

    expect(link(host, '/reports/gst')).not.toBeNull();
  });
});

/**
 * The top-level Super Admin group (issue #546, parent #510 under #328). The sidebar renders the
 * API's answer; it never decides it, and a hidden group protects nothing -
 * `/super-admin/diagnostics` and both diagnostics endpoints are independently authorized by the
 * API.
 */
describe('SidebarNavComponent Super Admin group (issue #546)', () => {
  it('offers no Super Admin group when the API does not confirm platform-admin access', async () => {
    const { host } = await render({ diagnosticsAccess: of(false) });

    expect(host.textContent).not.toContain('Super Admin');
    expect(host.textContent).not.toContain('Platform Diagnostics');
  });

  it('offers no Super Admin group while the access probe has not answered', async () => {
    const { host } = await render({ diagnosticsAccess: NEVER });

    expect(host.textContent).not.toContain('Super Admin');
  });

  it('offers no Super Admin group when the access probe fails', async () => {
    const { host } = await render({ diagnosticsAccess: throwError(() => new Error('probe failed')) });

    expect(host.textContent).not.toContain('Super Admin');
  });

  it('renders Platform Diagnostics, Businesses and Supplier Presets once the API confirms access', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: of(true) });
    expect(link(host, '/super-admin/diagnostics')).toBeNull();

    groupToggle(host, 'Super Admin').click();
    fixture.detectChanges();

    const diagnostics = link(host, '/super-admin/diagnostics');
    expect(diagnostics).not.toBeNull();
    expect(diagnostics?.textContent?.trim()).toBe('Platform Diagnostics');

    const businesses = link(host, '/super-admin/businesses');
    expect(businesses).not.toBeNull();
    expect(businesses?.textContent?.trim()).toBe('Businesses');

    const presets = link(host, '/super-admin/supplier-presets');
    expect(presets).not.toBeNull();
    expect(presets?.textContent?.trim()).toBe('Supplier Presets');
  });

  it('marks the diagnostics page active and opens the Super Admin group for it', async () => {
    const { host } = await render({ url: '/super-admin/diagnostics', diagnosticsAccess: of(true) });

    expect(groupToggle(host, 'Super Admin').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/super-admin/diagnostics')?.getAttribute('aria-current')).toBe('page');
  });

  it('adds no other destination when access is confirmed, besides its own three children', async () => {
    const { component } = await render({ diagnosticsAccess: of(true) });

    expect(component.navigation.map((entry) => entry.label)).toEqual([
      ...primaryNavigation.map((entry) => entry.label),
      'Super admin',
      'Super Admin'
    ]);
    expect(navLinks(component.navigation).map((entry) => entry.route)).toEqual([
      ...navLinks(primaryNavigation).map((entry) => entry.route),
      '/super-admin/diagnostics',
      '/super-admin/businesses',
      '/super-admin/supplier-presets'
    ]);
  });
});

/**
 * The menu a role preview renders (issue #721). The capability set is the API's own answer to
 * `GET /api/me/access` made under `X-Role-Preview`, so the sidebar needs no rule of its own for
 * it; the one thing the preview changes here is the `Super Admin` group, which belongs to no
 * business role and so cannot be part of what a previewed role is shown.
 */
describe('SidebarNavComponent during a role preview (issue #721)', () => {
  /** The capabilities the API answers an Operator with, as `navigation.spec.ts` pins them. */
  const OPERATOR = granted(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);

  it('renders exactly the Operator menu and hides the Super Admin group', async () => {
    const { component, host } = await render({
      access: of(OPERATOR),
      diagnosticsAccess: of(true),
      rolePreviewActive: true
    });

    expect(component.navigation.map((entry) => entry.label)).toEqual([
      'Operations',
      'Dashboard',
      'Pick List',
      'Sites',
      'Machines',
      'Inventory & purchasing',
      'Inventory',
      'Purchases',
      'Settings',
      'Plan'
    ]);
    expect(navLinks(component.navigation).map((entry) => entry.route)).toEqual([
      '/',
      '/pick-list',
      '/sites',
      '/machines',
      '/products',
      '/take-inventory',
      '/categories',
      '/stock-history',
      '/purchases',
      '/purchases/orders',
      '/suppliers',
      '/plan'
    ]);
    expect(host.textContent).not.toContain('Super Admin');
    expect(link(host, '/super-admin/businesses')).toBeNull();
  });

  it('reads no pending-applications badge while the group it belongs to is hidden', async () => {
    const pendingCount = jest.fn(() => of(3));
    await render({ diagnosticsAccess: of(true), rolePreviewActive: true, pendingCount });

    expect(pendingCount).not.toHaveBeenCalled();
  });

  it('restores the group as soon as the preview ends', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: of(true), rolePreviewActive: true });
    expect(host.textContent).not.toContain('Super Admin');

    fixture.componentRef.setInput('rolePreviewActive', false);
    fixture.detectChanges();

    expect(host.textContent).toContain('Super Admin');
  });
});

/**
 * The pending-business-applications badge on the `Super Admin` group and its `Businesses` child
 * (issue #547), re-read whenever the Businesses page reports a completed action.
 */
describe('SidebarNavComponent pending-business badge (issue #547)', () => {
  it('shows no badge while platform-admin access is not confirmed', async () => {
    const { host } = await render({ diagnosticsAccess: of(false), pendingCount: () => of(3) });

    expect(host.textContent).not.toContain('3');
  });

  it('shows the pending count on the group heading once access is confirmed', async () => {
    const { host } = await render({ diagnosticsAccess: of(true), pendingCount: () => of(2) });

    expect(groupToggle(host, 'Super Admin').textContent).toContain('2');
  });

  it('shows no badge while the count is zero', async () => {
    const { host } = await render({ diagnosticsAccess: of(true), pendingCount: () => of(0) });

    expect(groupToggle(host, 'Super Admin').textContent?.trim()).toBe('Super Admin▾');
  });

  it('shows the pending count beside the Businesses link once its group is open', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: of(true), pendingCount: () => of(5) });

    groupToggle(host, 'Super Admin').click();
    fixture.detectChanges();

    expect(link(host, '/super-admin/businesses')?.textContent).toContain('5');
  });

  it('re-reads the count when the Businesses page reports a completed action', async () => {
    const changed = new Subject<void>();
    const pendingCount = jest.fn().mockReturnValueOnce(of(4)).mockReturnValueOnce(of(1));
    const { fixture, host } = await render({
      diagnosticsAccess: of(true),
      pendingCount,
      businessesChanged$: changed
    });

    expect(groupToggle(host, 'Super Admin').textContent).toContain('4');

    changed.next();
    fixture.detectChanges();

    expect(groupToggle(host, 'Super Admin').textContent).toContain('1');
    expect(pendingCount).toHaveBeenCalledTimes(2);
  });

  it('clears a previously granted count the moment platform-admin access is lost', async () => {
    const diagnosticsAccess = new BehaviorSubject(true);
    const { fixture, host, component } = await render({ diagnosticsAccess, pendingCount: () => of(3) });

    expect(groupToggle(host, 'Super Admin').textContent).toContain('3');

    diagnosticsAccess.next(false);
    fixture.detectChanges();

    expect(component.pendingBusinessCount).toBeNull();
  });
});

/**
 * The badge and the composed `403` chain together (issue #673). Nothing here is doubled except
 * MSAL, the configured API base URL, the toast and the platform-admin probe: the real
 * `AccessService`, the real `SuperAdminBusinessesService`, the production `403` interceptors and
 * the really rendered sidebar are all installed, because the loop this covers lives in none of
 * them on its own. A refused `GET .../pending-count` refreshed the member capability cache, and
 * every capability emission asked this component for the count again.
 */
describe('SidebarNavComponent pending-count refusal (issue #673)', () => {
  const ACCESS_READ = '/api/me/access';
  const PENDING_COUNT = '/api/super-admin/businesses/pending-count';

  interface ComposedRender extends Rendered {
    httpMock: HttpTestingController;
    toastError: jest.Mock;
  }

  async function renderWithRealChain(): Promise<ComposedRender> {
    const identity = signedInIdentity();
    const toastError = jest.fn();

    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankPageComponent }]),
        provideHttpClient(withInterceptors([...FORBIDDEN_INTERCEPTORS])),
        provideHttpClientTesting(),
        // The shared provider-availability read (issues #728, #729) is stubbed, so the requests this
        // test asserts on stay the ones it is about.
        provideNayaxConnectionStatus(),
        { provide: ConfigService, useValue: apiConfig() },
        { provide: ToastService, useValue: { error: toastError } },
        { provide: MsalService, useValue: identity.msal },
        { provide: MsalBroadcastService, useValue: identity.broadcast },
        // Platform access was confirmed and is still cached, which is the state the loop needed:
        // the group is rendered, so the badge keeps asking for a count the server now refuses.
        { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(true) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SidebarNavComponent);
    fixture.componentRef.setInput('expanded', true);
    fixture.componentRef.setInput('businessNavigation', 'offered');
    fixture.detectChanges();

    return {
      fixture,
      component: fixture.componentInstance,
      host: fixture.nativeElement as HTMLElement,
      httpMock: TestBed.inject(HttpTestingController),
      toastError
    };
  }

  /**
   * Answers every outstanding request - the capability read with an Owner's own answer, everything
   * else with `403` - round after round until nothing is issuing any more, and returns the URLs in
   * the order they were answered. A chain that never settles is the defect, so the rounds are
   * capped and exhausting the cap fails loudly rather than hanging the suite.
   */
  function settle(rendered: ComposedRender, rounds = 12): string[] {
    const answered: string[] = [];

    for (let round = 0; round < rounds; round++) {
      const outstanding = rendered.httpMock.match(() => true);
      if (outstanding.length === 0) {
        rendered.fixture.detectChanges();
        return answered;
      }

      for (const request of outstanding) {
        answered.push(request.request.url);
        if (request.request.url === ACCESS_READ) {
          request.flush({
            role: 'Owner',
            capabilities: [...CAPABILITIES],
            businessName: 'Vending Co',
            timeZoneId: 'Australia/Sydney'
          });
        } else {
          request.flush('refused', { status: 403, statusText: 'Forbidden' });
        }
      }

      rendered.fixture.detectChanges();
    }

    throw new Error(`the chain was still issuing requests after ${rounds} rounds: ${answered.join(', ')}`);
  }

  it('settles instead of looping when the pending count keeps being refused', async () => {
    const rendered = await renderWithRealChain();

    // One read when the cached platform grant arrives and one when the capability set is first
    // answered, and then nothing: the refusals themselves drive no further request at all.
    expect(settle(rendered)).toEqual([ACCESS_READ, PENDING_COUNT, PENDING_COUNT]);
    rendered.httpMock.verify();
  });

  it('leaves the badge in a stable unavailable state, with the group still offered', async () => {
    const rendered = await renderWithRealChain();

    settle(rendered);

    expect(rendered.component.pendingBusinessCount).toBeNull();
    expect(groupToggle(rendered.host, 'Super Admin').textContent?.trim()).toBe('Super Admin▾');
  });

  it('tells the operator nothing and leaves the member capability set alone', async () => {
    const rendered = await renderWithRealChain();

    settle(rendered);

    expect(rendered.toastError).not.toHaveBeenCalled();
    // The capabilities were read once, for this identity, and the refusals never re-read them.
    expect(rendered.component.navigation.length).toBeGreaterThan(0);
  });
});

/**
 * The Super Admin group follows the signed-in identity (issue #605). The real
 * `PlatformDiagnosticsAccessService` is used here, with MSAL and the diagnostics HTTP call under
 * the test's control, because the regression this covers is not in the sidebar's rendering but in
 * what the cached probe answers after an account switch.
 */
describe('SidebarNavComponent Super Admin group across an identity change (issue #605)', () => {
  const administrator = { name: 'A', homeAccountId: 'a-home-id' } as unknown as AccountInfo;
  const ordinaryMember = { name: 'B', homeAccountId: 'b-home-id' } as unknown as AccountInfo;

  const authorizedResponse: PlatformDiagnosticsAccess = {
    authorized: true,
    crossBusinessScope: true,
    limits: { maxSqlBytes: 16384, maxRows: 500, maxResponseBytes: 1048576, maxDurationSeconds: 5 }
  };

  interface ProbedRender extends Rendered {
    switchTo(next: AccountInfo): void;
  }

  /** One diagnostics response per probe, in order; the last one answers every further probe. */
  function queued(...responses: readonly (() => Observable<PlatformDiagnosticsAccess>)[]) {
    let call = 0;
    return () => (responses[call++] ?? responses[responses.length - 1])();
  }

  async function renderWithRealProbe(
    signedIn: AccountInfo,
    access: () => Observable<PlatformDiagnosticsAccess>
  ): Promise<ProbedRender> {
    let activeAccount = signedIn;
    const events = new Subject<EventMessage>();

    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankPageComponent }]),
        provideNayaxConnectionStatus(),
        { provide: AccessService, useValue: { access$: of(EVERY_CAPABILITY) } },
        { provide: PlatformDiagnosticsService, useValue: { access, query: jest.fn() } },
        { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } },
        {
          provide: MsalService,
          useValue: {
            instance: { getActiveAccount: () => activeAccount, getAllAccounts: () => [activeAccount] }
          }
        },
        {
          provide: MsalBroadcastService,
          useValue: {
            inProgress$: new BehaviorSubject<InteractionStatus>(InteractionStatus.None),
            msalSubject$: events
          }
        }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SidebarNavComponent);
    fixture.componentRef.setInput('expanded', true);
    fixture.componentRef.setInput('businessNavigation', 'offered');
    fixture.detectChanges();

    return {
      fixture,
      component: fixture.componentInstance,
      host: fixture.nativeElement as HTMLElement,
      switchTo(next: AccountInfo): void {
        activeAccount = next;
        events.next({ eventType: EventType.ACTIVE_ACCOUNT_CHANGED } as EventMessage);
        fixture.detectChanges();
      }
    };
  }

  it('removes a granted group the moment the identity changes, before the new probe answers', async () => {
    const pending = new Subject<PlatformDiagnosticsAccess>();
    const rendered = await renderWithRealProbe(
      administrator,
      queued(
        () => of(authorizedResponse),
        () => pending
      )
    );
    expect(rendered.host.textContent).toContain('Super Admin');

    rendered.switchTo(ordinaryMember);

    expect(rendered.host.textContent).not.toContain('Super Admin');

    pending.next({ ...authorizedResponse, authorized: false });
    rendered.fixture.detectChanges();

    expect(rendered.host.textContent).not.toContain('Super Admin');
  });

  it('offers the group to a newly active identity whose own probe confirms access', async () => {
    const rendered = await renderWithRealProbe(
      ordinaryMember,
      queued(
        () => throwError(() => ({ status: 403, error: null })),
        () => of(authorizedResponse)
      )
    );
    expect(rendered.host.textContent).not.toContain('Super Admin');

    rendered.switchTo(administrator);

    expect(rendered.host.textContent).toContain('Super Admin');
  });
});

/**
 * The capability-driven menu (issue #528). The sidebar renders the capability set the access
 * service reported and decides nothing itself; a destination it does not render hides a page
 * rather than protecting one, because the endpoints behind that page authorize every request.
 */
describe('SidebarNavComponent capability-driven menu (issue #528)', () => {
  const operator = granted(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);
  const manager = granted([
    'Dashboard.View',
    'Dashboard.ViewFinancials',
    'PickList.View',
    'Inventory.Operate',
    'Purchasing.Operate',
    'Suppliers.ManageGstDefaults',
    'Expenses.Manage',
    'Reports.View',
    'Imports.Run',
    'SiteCommissions.Manage'
  ]);

  it('renders no destination at all until the capability set has been read', async () => {
    const { host } = await render({ access: NEVER });

    expect(host.querySelector('nav[aria-label="Primary"]')).not.toBeNull();
    expect(host.querySelectorAll('nav[aria-label="Primary"] a')).toHaveLength(0);
    expect(host.textContent).not.toContain('Expenses');
  });

  it('offers an Operator no Expenses link and no Reports or Admin heading', async () => {
    const { host } = await render({ access: of(operator) });

    expect(link(host, '/')).not.toBeNull();
    expect(link(host, '/pick-list')).not.toBeNull();
    expect(link(host, '/expenses')).toBeNull();
    expect(host.textContent).not.toContain('Expenses');
    expect(host.textContent).not.toContain('Reports');
    expect(host.textContent).not.toContain('Admin');
  });

  it('offers a Manager the Reports and Admin groups, without Integrations or a repair page', async () => {
    const { fixture, host } = await render({ access: of(manager) });
    groupToggle(host, 'Admin').click();
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    expect(link(host, '/expenses')).not.toBeNull();
    expect(link(host, '/reports/gst')).not.toBeNull();
    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/site-commission-agreements')).not.toBeNull();
    expect(link(host, '/admin/nayax-settings')).toBeNull();
    expect(link(host, '/admin/costing-repair')).toBeNull();
    expect(link(host, '/admin/historical-gst-classification')).toBeNull();
  });

  it('re-renders when the capability set is read again, so a role change reaches the menu', async () => {
    const access = new BehaviorSubject<MemberAccessState>(operator);
    const { fixture, host } = await render({ access });
    expect(link(host, '/expenses')).toBeNull();

    access.next(EVERY_CAPABILITY);
    fixture.detectChanges();

    expect(link(host, '/expenses')).not.toBeNull();
  });

  /**
   * The fail-open rule: the capability set could not be read, the API still refuses what the role
   * may not do, and an empty menu would strand a member whose capability read happened to fail.
   */
  it('offers every destination when the capability set could not be read', async () => {
    const { component } = await render({ access: of<MemberAccessState>({ status: 'unavailable' }) });

    expect(navLinks(component.navigation).map((entry) => entry.route)).toEqual(
      navLinks(primaryNavigation).map((entry) => entry.route)
    );
  });

  it('still offers the Super Admin group to an Operator the diagnostics API confirms, with no Admin group', async () => {
    const { fixture, host } = await render({ access: of(operator), diagnosticsAccess: of(true) });
    groupToggle(host, 'Super Admin').click();
    fixture.detectChanges();

    expect(link(host, '/super-admin/diagnostics')).not.toBeNull();
    expect(Array.from(host.querySelectorAll('button')).some((button) => button.textContent?.trim() === 'Admin')).toBe(
      false
    );
    expect(link(host, '/admin/nayax-settings')).toBeNull();
  });
});

/**
 * The menu after a Microsoft account change (issue #603). The sidebar holds one long-lived
 * subscription for the life of the shell, so it is the consumer that would otherwise keep
 * presenting the previous account's destinations for as long as the new account's read takes.
 */
describe('SidebarNavComponent menu across an account change (issue #603)', () => {
  const OWNER_RESPONSE = {
    role: 'Owner',
    capabilities: [...CAPABILITIES],
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney'
  };
  const OPERATOR_RESPONSE = {
    role: 'Operator',
    capabilities: ['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate'],
    businessName: 'Other Co',
    timeZoneId: 'Australia/Sydney'
  };

  /** The real service, with only its one `GET` and MSAL's two signals doubled. */
  function accessWithSwitch(reads: (() => unknown)[]): { access: AccessService; switchAccount(id: string): void } {
    const identity = signedInIdentity();
    const read = queuedReads(reads, OPERATOR_RESPONSE);
    const access = new AccessService(readingHttp(read), apiConfig(), identity.msal, identity.broadcast);

    return { access, switchAccount: identity.switchAccount };
  }

  it('offers no destination while the new account’s capabilities are still being read', async () => {
    const pending = new Subject<unknown>();
    const { access, switchAccount } = accessWithSwitch([() => of(OWNER_RESPONSE), () => pending]);
    const { fixture, host } = await render({ accessService: access });
    expect(link(host, '/expenses')).not.toBeNull();
    expect(host.textContent).toContain('Admin');

    switchAccount('member-2');
    fixture.detectChanges();

    expect(host.querySelectorAll('nav[aria-label="Primary"] a')).toHaveLength(0);
    expect(host.textContent).not.toContain('Admin');

    pending.next(OPERATOR_RESPONSE);
    pending.complete();
    fixture.detectChanges();

    expect(link(host, '/pick-list')).not.toBeNull();
    expect(link(host, '/expenses')).toBeNull();
    expect(host.textContent).not.toContain('Admin');
  });
});

/**
 * The sidebar's white panel must fill the available application height (issue #455): the shell
 * stretches `app-sidebar-nav` to the full height of the row beside `<main>`, but that stretch only
 * reaches the visible `<nav>` panel if the component's own host element contributes no box of its
 * own. A host that renders as an ordinary block traps the stretched height on the invisible host
 * and leaves the white `<nav>` sized to its content instead.
 */
describe('SidebarNavComponent host element (issue #455)', () => {
  it('renders as display:contents so the shell can stretch the nav panel itself, not an invisible wrapper', async () => {
    const { host } = await render();

    expect(host.classList.contains('contents')).toBe(true);
  });
});

/**
 * Outlined navigation glyphs (issue #456). The sidebar is the only caller that asks `app-icon` for
 * the Outlined set, and it must ask for it in every state the icons appear in, because an
 * expanded, collapsed and drawer sidebar render the same items through the same template.
 */
describe('SidebarNavComponent outlined icons (issue #456)', () => {
  const states: readonly { readonly name: string; readonly expanded: boolean; readonly drawer: boolean }[] = [
    { name: 'expanded desktop', expanded: true, drawer: false },
    { name: 'collapsed desktop', expanded: false, drawer: false },
    { name: 'mobile drawer', expanded: true, drawer: true }
  ];

  function renderedPaths(host: HTMLElement): string[] {
    return Array.from(host.querySelectorAll('nav[aria-label="Primary"] svg path')).map(
      (path) => path.getAttribute('d') ?? ''
    );
  }

  const outlinedPaths = new Set(Object.values(OUTLINED_ICON_SHAPES).flatMap((shapes) => shapes.paths));
  const roundedPaths = new Set(Object.values(ICON_PATHS));

  it.each(states)('draws only outlined geometry in the $name sidebar', async ({ expanded, drawer }) => {
    const { host } = await render({ expanded, drawer });
    const paths = renderedPaths(host);

    expect(paths.length).toBeGreaterThan(0);
    for (const path of paths) {
      expect(outlinedPaths.has(path)).toBe(true);
      expect(roundedPaths.has(path)).toBe(false);
    }
  });

  it('draws an outlined glyph for every declared destination and group icon', async () => {
    const { fixture, host } = await render({ expanded: true });
    for (const label of ['Inventory', 'Purchases', 'Reports', 'Admin']) {
      groupToggle(host, label).click();
    }
    fixture.detectChanges();

    // Every top-level entry carries an icon: since issue #868 removed the "Needs Nayax" heading,
    // every one of them is a destination or a group, and the rail has nothing else to draw.
    const iconBearing = primaryNavigation.filter((item) => item.kind !== 'section');
    const declaredIcons = iconBearing.flatMap((item) => (item.icon === undefined ? [] : [item.icon]));
    expect(iconBearing).toHaveLength(primaryNavigation.filter((item) => item.kind !== 'section').length);
    expect(declaredIcons).toHaveLength(iconBearing.length);

    for (const icon of declaredIcons) {
      for (const expected of OUTLINED_ICON_SHAPES[icon].paths) {
        expect(renderedPaths(host)).toContain(expected);
      }
    }
  });

  it('keeps the destination glyphs decorative, because the adjacent label names the destination', async () => {
    const { host } = await render({ expanded: true });
    const dashboard = link(host, '/');

    expect(dashboard?.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    expect(dashboard?.querySelector('svg')?.hasAttribute('role')).toBe(false);
    expect(dashboard?.textContent).toContain('Dashboard');
  });

  it('inherits the item colour, so the active item keeps a white icon on the dark gradient', async () => {
    const { host } = await render({ expanded: true, url: '/machines' });
    const active = link(host, '/machines');

    expect(active?.classList.contains('text-white')).toBe(true);
    expect(active?.querySelector('svg')?.getAttribute('fill')).toBe('currentColor');
  });
});

/**
 * The desktop collapse control lives in the sidebar's own header row, beside the app title
 * (issue #456). It replaces the detached toggle the shell's top bar used to show on a wide layout,
 * so the sidebar now owns the control and still owns none of the state: it reports the intent
 * through `collapseToggled` and the shell keeps deciding what `expanded` means.
 */
describe('SidebarNavComponent header collapse control (issue #456)', () => {
  function headerToggle(host: HTMLElement, navId = PRIMARY_NAVIGATION_ID): HTMLButtonElement | null {
    return host.querySelector<HTMLButtonElement>(`nav[aria-label="Primary"] > div button[aria-controls="${navId}"]`);
  }

  it('gives the navigation landmark the id the shell asks for, so a toggle can control it', async () => {
    const { host } = await render({ navId: 'shell-provided-navigation' });

    expect(host.querySelector('nav[aria-label="Primary"]')?.id).toBe('shell-provided-navigation');
  });

  it('renders a real labelled button in the same header row as the app title when expanded', async () => {
    const { host } = await render({ expanded: true });
    const toggle = headerToggle(host);

    expect(toggle).not.toBeNull();
    expect(toggle?.tagName).toBe('BUTTON');
    expect(toggle?.getAttribute('type')).toBe('button');
    expect(toggle?.getAttribute('aria-label')).toBe('Collapse navigation');
    expect(toggle?.getAttribute('aria-expanded')).toBe('true');
    expect(toggle?.getAttribute('aria-controls')).toBe(PRIMARY_NAVIGATION_ID);

    // Same header row as the title, with the title before it: the row is the toggle's parent.
    const row = toggle?.parentElement as HTMLElement;
    expect(row.textContent).toContain('Restockroo');
    expect(row.firstElementChild?.textContent).toContain('Restockroo');
  });

  it('reports the collapse intent instead of changing its own expanded state', async () => {
    const { fixture, component, host } = await render({ expanded: true });
    const collapseToggled = jest.fn();
    component.collapseToggled.subscribe(collapseToggled);

    headerToggle(host)?.click();
    fixture.detectChanges();

    expect(collapseToggled).toHaveBeenCalledTimes(1);
    // The shell owns the flag, so the sidebar is still expanded until it is told otherwise.
    expect(component.expanded).toBe(true);
    expect(headerToggle(host)?.getAttribute('aria-expanded')).toBe('true');
  });

  it('keeps a reachable expand button, and its reversed label and state, while collapsed', async () => {
    const { fixture, component, host } = await render({ expanded: false });
    const toggle = headerToggle(host);

    expect(toggle).not.toBeNull();
    expect(toggle?.getAttribute('aria-label')).toBe('Expand navigation');
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(toggle?.getAttribute('aria-controls')).toBe(PRIMARY_NAVIGATION_ID);
    // Visible, not an sr-only control, and not hidden from assistive technology.
    expect(toggle?.classList.contains('hidden')).toBe(false);
    expect(toggle?.classList.contains('sr-only')).toBe(false);
    expect(toggle?.getAttribute('aria-hidden')).toBeNull();

    const collapseToggled = jest.fn();
    component.collapseToggled.subscribe(collapseToggled);
    toggle?.click();
    fixture.detectChanges();

    expect(collapseToggled).toHaveBeenCalledTimes(1);
  });

  // JSDOM computes no layout, so the 44x44 CSS-pixel minimum touch target the issue requires is
  // asserted on the sizing utilities that produce it (`h-11`/`w-11` are 2.75rem = 44px), and on
  // the `shrink-0` that stops a long title squeezing it below that.
  it.each([true, false])('keeps the 44x44 touch target and never shrinks (expanded: %s)', async (expanded) => {
    const { host } = await render({ expanded });
    const classes = Array.from(headerToggle(host)?.classList ?? []);

    expect(classes).toContain('h-11');
    expect(classes).toContain('w-11');
    expect(classes).toContain('shrink-0');
  });

  it('leaves the app title room of its own so the two never overlap', async () => {
    const { host } = await render({ expanded: true });
    const title = headerToggle(host)?.parentElement?.firstElementChild as HTMLElement;

    expect(Array.from(title.classList)).toEqual(expect.arrayContaining(['min-w-0', 'truncate']));
  });

  it('offers no collapse control in the narrow drawer, which closes instead of collapsing', async () => {
    const { host } = await render({ drawer: true });

    expect(headerToggle(host)).toBeNull();
    expect(host.querySelector('button[aria-label="Collapse navigation"]')).toBeNull();
    expect(host.querySelector('button[aria-label="Expand navigation"]')).toBeNull();
    expect(host.querySelector('button[aria-label="Close navigation menu"]')).not.toBeNull();
  });

  it('changes no destination, group or active-state behaviour when it is used', async () => {
    const { fixture, host } = await render({ expanded: true, url: '/reports/gst' });
    const before = Array.from(host.querySelectorAll('a')).map((anchor) => anchor.getAttribute('href'));

    headerToggle(host)?.click();
    fixture.detectChanges();

    expect(Array.from(host.querySelectorAll('a')).map((anchor) => anchor.getAttribute('href'))).toEqual(before);
    expect(groupToggle(host, 'Reports').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/reports/gst')?.getAttribute('aria-current')).toBe('page');
  });
});

describe('SidebarNavComponent drawer behaviour (issue #391)', () => {
  it('offers no dismiss control when the sidebar is part of the wide layout', async () => {
    const { host } = await render({ drawer: false });

    expect(host.querySelector('button[aria-label="Close navigation menu"]')).toBeNull();
  });

  it('dismisses the drawer from its own close control', async () => {
    const { component, host } = await render({ drawer: true });
    const dismissed = jest.fn();
    component.dismissed.subscribe(dismissed);

    const close = host.querySelector<HTMLButtonElement>('button[aria-label="Close navigation menu"]');
    expect(close).not.toBeNull();
    close?.click();

    expect(dismissed).toHaveBeenCalledTimes(1);
  });

  /**
   * The More sheet (issue #972): a modal dialog with a sticky Close control that takes focus as it
   * opens, keeps Tab inside itself, and opens every group so the whole menu scrolls.
   */
  it('renders the narrow menu as a labelled modal sheet that focuses Close and opens every group', async () => {
    const { host } = await render({ drawer: true });
    const sheet = host.querySelector<HTMLElement>('[data-testid="more-sheet"]');

    expect(sheet?.tagName).toBe('DIALOG');
    expect((sheet as HTMLDialogElement).open).toBe(true);
    expect(sheet?.getAttribute('aria-label')).toBe('Menu');
    expect(sheet?.querySelector('nav[aria-label="Primary"]')).not.toBeNull();
    expect(document.activeElement).toBe(host.querySelector('button[aria-label="Close navigation menu"]'));
    for (const group of ['Inventory', 'Purchases', 'Reports', 'Admin']) {
      expect(groupToggle(host, group).getAttribute('aria-expanded')).toBe('true');
    }
    expect(link(host, '/admin/business-settings')).not.toBeNull();
  });

  it('keeps Tab and Shift+Tab inside the open sheet', async () => {
    const { host } = await render({ drawer: true });
    const sheet = host.querySelector<HTMLElement>('[data-testid="more-sheet"]') as HTMLElement;
    const focusable = Array.from(sheet.querySelectorAll<HTMLElement>('a[href], button:not([disabled])'));
    const first = focusable[0];
    const last = focusable[focusable.length - 1];

    last.focus();
    sheet.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(first);

    first.focus();
    sheet.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(last);
  });

  /**
   * The dialog's own close request (Escape, or a back gesture) is cancelled and reported, so the
   * shell - not the browser - closes the menu and returns focus to the opener.
   */
  it('reports the dialog close request instead of letting the browser close it', async () => {
    const { component, host } = await render({ drawer: true });
    const dismissed = jest.fn();
    component.dismissed.subscribe(dismissed);
    const cancel = new Event('cancel', { cancelable: true });

    host.querySelector('[data-testid="more-sheet"]')?.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
    expect(dismissed).toHaveBeenCalledTimes(1);
  });

  it('dismisses on a click on the backdrop, and not on a click inside the sheet', async () => {
    const { component, host } = await render({ drawer: true });
    const dismissed = jest.fn();
    component.dismissed.subscribe(dismissed);
    const sheet = host.querySelector<HTMLElement>('[data-testid="more-sheet"]') as HTMLElement;

    sheet.querySelector('nav')?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(dismissed).not.toHaveBeenCalled();

    sheet.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(dismissed).toHaveBeenCalledTimes(1);
  });

  it('renders no sheet or dialog on a wide layout', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="more-sheet"]')).toBeNull();
    expect(host.querySelector('dialog')).toBeNull();
  });

  it('reports every navigation so the shell can dismiss a narrow drawer', async () => {
    const { fixture, component, host } = await render({ drawer: true });
    const navigated = jest.fn();
    component.navigated.subscribe(navigated);

    link(host, '/pick-list')?.click();
    fixture.detectChanges();
    expect(navigated).toHaveBeenCalledTimes(1);

    // The More sheet opens every group (issue #972), so a child is one press away.
    link(host, '/categories')?.click();

    expect(navigated).toHaveBeenCalledTimes(2);
  });
});

/**
 * What the sidebar renders when the shell withholds the business navigation (issue #676). The
 * decision is the shell's - this component conditions nothing in its template and owns no part of
 * it - so here it arrives as the input it is, with the capability answers that make the two cases
 * it has to tell apart indistinguishable on their own.
 */
describe('SidebarNavComponent with the business navigation withheld (issue #676)', () => {
  const UNAVAILABLE: MemberAccessState = { status: 'unavailable' };

  /** Every rendered destination that is not the separately authorized Super Admin group's. */
  function businessDestinations(host: HTMLElement): string[] {
    return Array.from(host.querySelectorAll('a'))
      .map((anchor) => anchor.getAttribute('href') ?? '')
      .filter((href) => !href.startsWith('/super-admin'));
  }

  it('renders no business destination, and still renders the navigation landmark', async () => {
    const { host } = await render({ businessNavigation: 'withheld', access: of(EVERY_CAPABILITY) });

    expect(businessDestinations(host)).toEqual([]);
    expect(host.querySelector(`nav[aria-label="Primary"]`)).not.toBeNull();
  });

  /**
   * The conflation this issue exists for: a signed-out visitor's and a known nonmember's
   * `GET /api/me/access` both answer `unavailable`, the one answer that offers every destination.
   */
  it('renders none for a capability set that could not be read either', async () => {
    const { host } = await render({ businessNavigation: 'withheld', access: of(UNAVAILABLE) });

    expect(businessDestinations(host)).toEqual([]);
    expect(host.textContent).not.toContain('Dashboard');
  });

  /** The same unreadable capability answer, for somebody the account state confirmed is a member. */
  it('keeps every destination for an offered menu whose capability set could not be read', async () => {
    const { host } = await render({ businessNavigation: 'offered', access: of(UNAVAILABLE) });

    expect(businessDestinations(host)).toEqual(
      expect.arrayContaining(['/', '/pick-list', '/machines', '/sites', '/expenses'])
    );
  });

  it('keeps the Super Admin group for a platform administrator with no business menu', async () => {
    const { host } = await render({
      businessNavigation: 'withheld',
      access: of(UNAVAILABLE),
      diagnosticsAccess: of(true)
    });

    expect(businessDestinations(host)).toEqual([]);
    expect(groupToggle(host, 'Super Admin')).not.toBeNull();
  });

  it('fills the menu in when the shell offers it, without being re-created', async () => {
    const { fixture, host } = await render({ businessNavigation: 'withheld', access: of(EVERY_CAPABILITY) });
    expect(businessDestinations(host)).toEqual([]);

    fixture.componentRef.setInput('businessNavigation', 'offered');
    fixture.detectChanges();

    expect(link(host, '/pick-list')).not.toBeNull();

    fixture.componentRef.setInput('businessNavigation', 'withheld');
    fixture.detectChanges();

    expect(businessDestinations(host)).toEqual([]);
  });

  /** The input is what says so: nothing is offered to a host that never answered the question. */
  it('renders no business destination until a host says the menu is offered', async () => {
    const connections = nayaxConnectionStatusDouble('Ready');

    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankPageComponent }]),
        { provide: NayaxConnectionService, useValue: connections },
        { provide: AccessService, useValue: { access$: of(EVERY_CAPABILITY) } },
        { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(false) } },
        { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SidebarNavComponent);
    fixture.detectChanges();

    expect(businessDestinations(fixture.nativeElement as HTMLElement)).toEqual([]);
    // And the provider status was never asked for: an account state with no business menu resolves
    // no business, so there is no connection to read a status for (issue #729).
    expect(connections.status).not.toHaveBeenCalled();
  });
});

/**
 * The provider-locked menu (issue #729, child E2 of issue #329, decision P6): the live provider
 * section, what a locked entry renders as, and the repair tools that are hidden outright. Every
 * answer is derived by the shared availability signal of issue #728 from a stubbed status read, so
 * what is asserted here is what the application actually decides, not a shape this spec invented.
 */
describe('SidebarNavComponent provider-locked destinations (issue #729)', () => {
  const PERMISSION_REFUSED: readonly NayaxConnectionCheckLine[] = [
    { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' },
    { check: 'MachineLastSales', status: 'Passed', failure: null }
  ];

  /** The five reports issue #868 locks while the business has no Nayax connection at all. */
  const PROVIDER_RECORD_REPORTS = [
    '/reports/daily',
    '/reports/transactions',
    '/reports/reconciliation',
    '/reports/machines',
    '/reports/products'
  ];

  function lockBadges(host: HTMLElement, route: string): string {
    return link(host, route)?.textContent ?? '';
  }

  /**
   * Issue #868 removed the "Needs Nayax" heading: nothing in the menu is grouped by what it needs
   * from the provider any more, and `Sites` sits immediately above `Machines` as an ordinary
   * top-level destination.
   */
  it('renders no provider section heading, with Sites immediately above Machines', async () => {
    const { host, component } = await render();
    const headings = Array.from(host.querySelectorAll('nav[aria-label="Primary"] h2')).map((entry) =>
      (entry.textContent ?? '').trim()
    );

    // Only the Restockroo group headings of issue #972, none of which is about the provider.
    expect(headings).toEqual(['Operations', 'Inventory & purchasing', 'Finance & reports', 'Settings']);
    expect(host.textContent).not.toContain('Needs Nayax');
    expect(link(host, '/sites')).not.toBeNull();
    expect(link(host, '/machines')).not.toBeNull();
    const labels = component.navigation.map((entry) => entry.label);
    expect(labels.indexOf('Machines')).toBe(labels.indexOf('Sites') + 1);
  });

  /** Site Commissions is a Reports child again (issue #868), rendered once its group is open. */
  it('renders Site Commissions inside the Reports group, not as a top-level entry', async () => {
    const { fixture, host } = await render();

    expect(link(host, '/reports/site-commissions')).toBeNull();

    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    expect(link(host, '/reports/site-commissions')).not.toBeNull();
  });

  /**
   * The order every role is shown, which is the same order for all of them: what each one is
   * offered differs only by capability, never by where an entry sits. Issue #972's group headings
   * are part of it, and go with the entries under them.
   */
  const OWNER_ORDER = [
    'Operations',
    'Dashboard',
    'Pick List',
    'Sites',
    'Machines',
    'Inventory & purchasing',
    'Inventory',
    'Purchases',
    'Finance & reports',
    'Expenses',
    'Reports',
    'Settings',
    'Plan',
    'Admin'
  ];
  it.each([
    {
      role: 'an Owner',
      capabilities: CAPABILITIES,
      expected: OWNER_ORDER
    },
    {
      role: 'a Manager',
      capabilities: [
        'Dashboard.View',
        'Dashboard.ViewFinancials',
        'PickList.View',
        'Inventory.Operate',
        'Purchasing.Operate',
        'Suppliers.ManageGstDefaults',
        'Expenses.Manage',
        'Reports.View',
        'Imports.Run',
        'SiteCommissions.Manage'
      ] as readonly Capability[],
      expected: OWNER_ORDER
    },
    {
      role: 'a Reports-only role',
      capabilities: ['Reports.View'] as readonly Capability[],
      expected: ['Finance & reports', 'Reports']
    }
  ])('renders the menu in one order for $role', async ({ capabilities, expected }) => {
    const { component } = await render({ access: of(granted(capabilities)), providerStatus: 'NotConfigured' });

    expect(component.navigation.map((entry) => entry.label)).toEqual(expected);
  });

  it('renames the Products group Inventory, leaving the Products page its own name and URL', async () => {
    const { fixture, host } = await render();
    groupToggle(host, 'Inventory').click();
    fixture.detectChanges();

    expect(link(host, '/products')?.textContent).toContain('Products');
    expect(link(host, '/take-inventory')).not.toBeNull();
  });

  it('locks nothing on a usable connection', async () => {
    const { fixture, host } = await render({ providerStatus: 'Ready' });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    expect(lockBadges(host, '/machines')).not.toContain('Locked');
    expect(lockBadges(host, '/sites')).not.toContain('Locked');
    expect(lockBadges(host, '/reports/site-commissions')).not.toContain('Locked');
  });

  /**
   * A locked entry keeps its link: clicking it is how an operator reaches the explanation, and the
   * route's own `requireProvider` guard is what shows it. So it is marked, never disabled.
   */
  it('shows every live provider entry locked, and still linked, with no connection at all', async () => {
    const { fixture, host } = await render({ providerStatus: 'NotConfigured' });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    for (const route of ['/sites', '/reports/site-commissions']) {
      const entry = link(host, route);
      expect(entry).not.toBeNull();
      expect(entry?.textContent).toContain('Locked');
      expect(entry?.hasAttribute('aria-disabled')).toBe(false);
      expect(entry?.getAttribute('title')).toContain('Nayax is not connected');
    }

    // The machine list is never locked since issue #740: the page has the business's own machine
    // records to show whatever the provider's state is.
    expect(lockBadges(host, '/machines')).not.toContain('Locked');
    expect(link(host, '/machines')?.getAttribute('title')).toBeNull();
  });

  it('locks them for credentials that are stored and known not to work', async () => {
    const { host } = await render({ providerStatus: 'NeedsAttention' });

    expect(lockBadges(host, '/sites')).toContain('Locked');
    expect(lockBadges(host, '/machines')).not.toContain('Locked');
  });

  /**
   * The per-feature case of decision P9: in `PendingPermissions` the connection is usable and the
   * entry whose own read Nayax refused is locked as a **permission**, which is a different message
   * and a different fix from a missing connection.
   */
  it('locks a refused feature as a Nayax permission, naming the destination', async () => {
    const { host } = await render({ providerStatus: 'PendingPermissions', providerChecks: PERMISSION_REFUSED });

    const sites = link(host, '/sites');
    expect(sites?.textContent).toContain('Locked');
    expect(sites?.getAttribute('title')).toBe('Sites is waiting for a Nayax permission.');
    expect(sites?.getAttribute('title')).not.toContain('not connected');
  });

  it('leaves a destination whose read Nayax granted unlocked beside a refused one', async () => {
    const { host } = await render({
      providerStatus: 'PendingPermissions',
      providerChecks: [
        { check: 'Machines', status: 'Passed', failure: null },
        { check: 'MachineLastSales', status: 'Failed', failure: 'PermissionNotGranted' }
      ]
    });

    expect(lockBadges(host, '/sites')).not.toContain('Locked');
  });

  /** An unread status is not a refusal, so a failed probe locks nothing (issue #728's rule). */
  it('locks nothing when the connection status could not be read', async () => {
    const { host } = await render({ providerStatus: 'unreadable' });

    expect(lockBadges(host, '/sites')).not.toContain('Locked');
    expect(link(host, '/sites')?.getAttribute('title')).toBeNull();
  });

  /**
   * The Reports group with no Nayax connection at all (issue #868): every report is still offered,
   * the five over stored provider records are shown **locked** rather than hidden - clicking one is
   * how an operator reaches the explanation - and Bookkeeping and GST are untouched.
   */
  it('locks the five stored-record reports with no connection, leaving Bookkeeping and GST open', async () => {
    const { fixture, host } = await render({ providerStatus: 'NotConfigured' });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    for (const route of PROVIDER_RECORD_REPORTS) {
      const entry = link(host, route);
      expect(entry).not.toBeNull();
      expect(entry?.textContent).toContain('Locked');
      expect(entry?.hasAttribute('aria-disabled')).toBe(false);
      expect(entry?.getAttribute('title')).toContain('Nayax is not connected');
    }

    for (const route of ['/reports/bookkeeping', '/reports/gst']) {
      expect(link(host, route)).not.toBeNull();
      expect(lockBadges(host, route)).not.toContain('Locked');
      expect(link(host, route)?.getAttribute('title')).toBeNull();
    }
  });

  /**
   * Credentials that are stored and no longer work lock a live fleet destination and leave these
   * five open: the sales they report on are stored, and the connection they need only has to
   * exist. This is the state that proves `providerRecords` is not a synonym for a usable
   * connection.
   */
  it('leaves the five stored-record reports unlocked for credentials that stopped working', async () => {
    const { fixture, host } = await render({ providerStatus: 'NeedsAttention' });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    for (const route of PROVIDER_RECORD_REPORTS) {
      expect(lockBadges(host, route)).not.toContain('Locked');
    }
    // Beside them, the two destinations that do need a live read are locked.
    expect(lockBadges(host, '/sites')).toContain('Locked');
    expect(lockBadges(host, '/reports/site-commissions')).toContain('Locked');
  });

  it('leaves them unlocked on a usable connection whose fleet read Nayax refused', async () => {
    const { fixture, host } = await render({ providerStatus: 'PendingPermissions', providerChecks: PERMISSION_REFUSED });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    for (const route of PROVIDER_RECORD_REPORTS) {
      expect(lockBadges(host, route)).not.toContain('Locked');
    }
    expect(link(host, '/reports/site-commissions')?.getAttribute('title')).toBe(
      'Site Commissions is waiting for a Nayax permission.'
    );
  });

  it.each(['Ready', 'unreadable'] as const)(
    'leaves them unlocked on a connection that is %s with nothing refused',
    async (providerStatus) => {
      const { fixture, host } = await render({ providerStatus });
      groupToggle(host, 'Reports').click();
      fixture.detectChanges();

      for (const route of PROVIDER_RECORD_REPORTS) {
        expect(lockBadges(host, route)).not.toContain('Locked');
        expect(link(host, route)?.getAttribute('title')).toBeNull();
      }
    }
  );

  /** A locked report says what would unlock it, to a screen reader as well as in the tooltip. */
  it('gives a locked report the same sentence in its tooltip and for a screen reader', async () => {
    const { fixture, host } = await render({ providerStatus: 'NotConfigured' });
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();

    const daily = link(host, '/reports/daily');
    expect(daily?.getAttribute('title')).toBe('Daily is unavailable because Nayax is not connected.');
    expect(daily?.querySelector('.sr-only')?.textContent).toBe('Daily is unavailable because Nayax is not connected.');
  });

  it('hides the Nayax imports and the sale timestamp repair until the connection is usable', async () => {
    const { fixture, host } = await render({ providerStatus: 'NotConfigured' });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, '/admin/imports')).toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).toBeNull();
    // Integrations is where the connection is made, so it is always reachable, and the costing
    // repairs are the business's own data rather than the provider's.
    expect(link(host, '/admin/nayax-settings')?.textContent).toContain('Integrations');
    expect(link(host, '/admin/costing-repair')).not.toBeNull();
  });

  it('offers them again on a usable connection', async () => {
    const { fixture, host } = await render({ providerStatus: 'Ready' });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).not.toBeNull();
  });

  /**
   * The two Admin destinations issue #869 locks. They are locked exactly as Site Commissions is -
   * marked, still linked, with the sentence that says what would unlock them - and never hidden,
   * which is what keeps them apart from the two provider tools above.
   */
  describe('the two locked Admin destinations (issue #869)', () => {
    const LOCKED_ADMIN = ['/admin/site-commission-agreements', '/admin/avco-transition'];

    async function openAdmin(providerStatus: SpecConnectionStatus, providerChecks?: readonly NayaxConnectionCheckLine[]) {
      const rendered = await render({ providerStatus, providerChecks });
      groupToggle(rendered.host, 'Admin').click();
      rendered.fixture.detectChanges();
      return rendered;
    }

    it('shows both locked, and still linked, with no connection at all', async () => {
      const { host } = await openAdmin('NotConfigured');

      for (const route of LOCKED_ADMIN) {
        const entry = link(host, route);
        expect(entry).not.toBeNull();
        expect(entry?.textContent).toContain('Locked');
        expect(entry?.hasAttribute('aria-disabled')).toBe(false);
        expect(entry?.getAttribute('title')).toContain('Nayax is not connected');
      }
    });

    it('locks both for credentials that are stored and known not to work', async () => {
      const { host } = await openAdmin('NeedsAttention');

      for (const route of LOCKED_ADMIN) {
        expect(link(host, route)?.textContent).toContain('Locked');
      }
    });

    /** A refused fleet read is the other lock, and it reads as a Nayax permission, not a connection. */
    it('locks both as a Nayax permission when that read was refused', async () => {
      const { host } = await openAdmin('PendingPermissions', PERMISSION_REFUSED);

      for (const route of LOCKED_ADMIN) {
        expect(link(host, route)?.getAttribute('title')).toContain('waiting for a Nayax permission');
      }
    });

    it.each(['Ready', 'PendingPermissions', 'unreadable'] as const)(
      'leaves both unlocked on a connection that is %s with nothing refused',
      async (providerStatus) => {
        const { host } = await openAdmin(providerStatus);

        for (const route of LOCKED_ADMIN) {
          expect(link(host, route)?.textContent).not.toContain('Locked');
        }
      }
    );

    /**
     * Nayax Historical Cost Recovery is never locked (the user's decision of 24 October 2026): it
     * reads the Nayax cost already stored on imported sales and makes no Nayax call, so a
     * disconnected business with imported history can still recover those costs.
     */
    it.each(['NotConfigured', 'NeedsAttention'] as const)(
      'never locks Nayax Historical Cost Recovery beside them, on a %s connection',
      async (providerStatus) => {
        const { host } = await openAdmin(providerStatus);
        const recovery = link(host, '/admin/historical-cost-recovery');

        expect(recovery).not.toBeNull();
        expect(recovery?.textContent).not.toContain('Locked');
        expect(recovery?.getAttribute('title')).toBeNull();
      }
    );

    /** Capability rules apply first: a Manager sees the agreements page, locked, and no AVCO entry. */
    it('offers a Manager the locked agreements page and no AVCO Transition entry at all', async () => {
      const manager = granted([
        'Dashboard.View',
        'Dashboard.ViewFinancials',
        'PickList.View',
        'Inventory.Operate',
        'Purchasing.Operate',
        'Expenses.Manage',
        'Reports.View',
        'Imports.Run',
        'SiteCommissions.Manage'
      ]);
      const { fixture, host } = await render({ access: of(manager), providerStatus: 'NotConfigured' });
      groupToggle(host, 'Admin').click();
      fixture.detectChanges();

      expect(link(host, '/admin/site-commission-agreements')?.textContent).toContain('Locked');
      expect(link(host, '/admin/avco-transition')).toBeNull();
    });
  });

  /** Capability rules apply first: an entry the role cannot see stays hidden, never locked. */
  it('hides a live provider entry the role may not see rather than locking it', async () => {
    const operator = granted(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);
    const { component, host } = await render({ access: of(operator), providerStatus: 'NotConfigured' });

    expect(lockBadges(host, '/sites')).toContain('Locked');
    // The whole Reports group is withheld from an Operator, so none of the locked reports - or the
    // site commission report - is offered as a locked entry either.
    expect(component.navigation.map((entry) => entry.label)).not.toContain('Reports');
    expect(navLinks(component.navigation).map((entry) => entry.route)).not.toContain('/reports/site-commissions');
    for (const route of PROVIDER_RECORD_REPORTS) {
      expect(navLinks(component.navigation).map((entry) => entry.route)).not.toContain(route);
    }
  });

  it('reads the connection status once however many entries depend on it', async () => {
    const connections = nayaxConnectionStatusDouble('NotConfigured');

    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        provideRouter([{ path: '**', component: BlankPageComponent }]),
        { provide: NayaxConnectionService, useValue: connections },
        { provide: AccessService, useValue: { access$: of(EVERY_CAPABILITY) } },
        { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(false) } },
        { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SidebarNavComponent);
    fixture.componentRef.setInput('businessNavigation', 'offered');
    fixture.detectChanges();

    expect(connections.status).toHaveBeenCalledTimes(1);
    expect(link(fixture.nativeElement as HTMLElement, '/sites')?.textContent).toContain('Locked');
  });
});

/**
 * The menu follows the shared answer for as long as it is mounted (issue #729). An Owner connects
 * Nayax in Integrations, loses the credentials, has one read refused, or starts a support session
 * on another business without ever leaving the page - and a menu built from the answer read when
 * the sidebar was created would go on locking what now works, or offering what no longer does,
 * while `requireProvider` asks again on every navigation and reaches the opposite verdict. One
 * destination may not have two answers, so every transition below is asserted on the **same**
 * sidebar, never on a recreated one.
 */
describe('SidebarNavComponent provider changes while it stays mounted (issue #729)', () => {
  function lockText(host: HTMLElement, route: string): string {
    return link(host, route)?.textContent ?? '';
  }

  function availability(): ProviderAvailabilityService {
    return TestBed.inject(ProviderAvailabilityService);
  }

  function connected(status: NayaxConnectionStatusName): NayaxConnectionState {
    return { status, machineChecksPending: false };
  }

  /** A `409` exactly as the API writes it when Nayax refused one read for lack of permission. */
  function permissionRefused(): HttpErrorResponse {
    return new HttpErrorResponse({ status: 409, error: { code: 'nayax_permission_not_granted' } });
  }

  /** The sidebar on its own, for the tests that need a router of their own as well. */
  async function mount(providers: readonly (Provider | EnvironmentProviders)[]): Promise<Rendered> {
    await TestBed.configureTestingModule({
      imports: [SidebarNavComponent],
      providers: [
        { provide: AccessService, useValue: { access$: of(EVERY_CAPABILITY) } },
        { provide: PlatformDiagnosticsAccessService, useValue: { granted$: of(false) } },
        { provide: SuperAdminBusinessesService, useValue: { pendingCount: () => of(0), changed$: NEVER } },
        ...providers
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(SidebarNavComponent);
    fixture.componentRef.setInput('businessNavigation', 'offered');
    fixture.detectChanges();

    return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
  }

  it('unlocks the live entries and offers the provider tools when a connection is saved', async () => {
    const { fixture, host } = await render({ providerStatus: 'NotConfigured' });
    groupToggle(host, 'Admin').click();
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();
    expect(lockText(host, '/sites')).toContain('Locked');
    expect(lockText(host, '/reports/daily')).toContain('Locked');
    expect(link(host, '/admin/imports')).toBeNull();

    availability().accept(connected('Ready'));
    fixture.detectChanges();

    for (const route of ['/sites', '/reports/site-commissions', '/reports/daily']) {
      expect(lockText(host, route)).not.toContain('Locked');
      expect(link(host, route)?.getAttribute('title')).toBeNull();
    }
    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).not.toBeNull();
  });

  it('locks them again, and withdraws the provider tools, when the credentials stop working', async () => {
    const { fixture, host } = await render({ providerStatus: 'Ready' });
    groupToggle(host, 'Admin').click();
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();
    expect(lockText(host, '/sites')).not.toContain('Locked');
    expect(link(host, '/admin/imports')).not.toBeNull();

    availability().accept(connected('NeedsAttention'));
    fixture.detectChanges();

    expect(lockText(host, '/sites')).toContain('Locked');
    expect(link(host, '/sites')?.getAttribute('title')).toBe('Sites is unavailable because Nayax is not connected.');
    expect(link(host, '/admin/imports')).toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).toBeNull();
    // The reports over stored provider records stay open: the connection still exists (issue #868).
    expect(lockText(host, '/reports/daily')).not.toContain('Locked');
    expect(lockText(host, '/reports/products')).not.toContain('Locked');
  });

  /**
   * And the state those five do lock in, reached without the sidebar ever being recreated: the
   * business's connection is removed, so the menu locks them there and then and agrees with
   * `requireProvider`, which asks again on the next navigation.
   */
  it('locks the five stored-record reports when the connection is removed', async () => {
    const answers: NayaxConnectionStatusName[] = ['Ready', 'NotConfigured'];
    const connections = {
      status: jest.fn(() => of(connected(answers.shift() ?? 'NotConfigured')))
    } as unknown as NayaxConnectionService;
    const { fixture, host } = await mount([
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      { provide: NayaxConnectionService, useValue: connections }
    ]);
    groupToggle(host, 'Reports').click();
    fixture.detectChanges();
    expect(lockText(host, '/reports/daily')).not.toContain('Locked');

    availability().reset();
    fixture.detectChanges();

    expect(lockText(host, '/reports/daily')).toContain('Locked');
    expect(link(host, '/reports/daily')?.getAttribute('title')).toBe(
      'Daily is unavailable because Nayax is not connected.'
    );
    expect(lockText(host, '/reports/bookkeeping')).not.toContain('Locked');
  });

  /**
   * A refusal retained by a page's own request is evidence the menu has to render too: the
   * connection is still working, so the tools stay and the message names a permission rather than
   * a missing connection.
   */
  it('locks a destination as a Nayax permission when a refusal is retained for its read', async () => {
    const { fixture, host } = await render({ providerStatus: 'PendingPermissions' });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();
    expect(lockText(host, '/sites')).not.toContain('Locked');

    availability().noteRefusal(permissionRefused(), 'machineFleet');
    fixture.detectChanges();

    expect(lockText(host, '/sites')).toContain('Locked');
    expect(link(host, '/sites')?.getAttribute('title')).toBe('Sites is waiting for a Nayax permission.');
    // The connection itself is unaffected, so the repair tools are still offered.
    expect(link(host, '/admin/imports')).not.toBeNull();
  });

  /**
   * A support session starts or ends on another business (issues #669, #670): the previous
   * business's answer is forgotten, and the menu asks for this one's rather than keeping locks that
   * were about a connection it no longer resolves.
   */
  it('renders the new business\'s answer when the context is reset, the menu staying offered', async () => {
    const answers: NayaxConnectionStatusName[] = ['Ready', 'NotConfigured'];
    const connections = {
      status: jest.fn(() => of(connected(answers.shift() ?? 'NotConfigured')))
    } as unknown as NayaxConnectionService;
    const { fixture, host } = await mount([
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      { provide: NayaxConnectionService, useValue: connections }
    ]);
    expect(lockText(host, '/sites')).not.toContain('Locked');

    availability().reset();
    fixture.detectChanges();

    expect(connections.status).toHaveBeenCalledTimes(2);
    expect(lockText(host, '/sites')).toContain('Locked');
  });

  /**
   * The agreement the locked menu exists for: after the same change, the entry and the URL answer
   * the same way. The route is guarded by the real `requireProvider`, so what is compared is the
   * application's two surfaces rather than two copies of one rule.
   */
  it('agrees with the route guard about a destination before and after the connection changes', async () => {
    const { fixture, host } = await mount([
      provideRouter([
        { path: 'sites', canActivate: [requireProvider('machineFleet')], component: BlankPageComponent },
        { path: PROVIDER_LOCKED_PATH, component: BlankPageComponent },
        { path: '**', component: BlankPageComponent }
      ]),
      provideNayaxConnectionStatus('NotConfigured')
    ]);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/sites');
    fixture.detectChanges();
    expect(router.url).toBe(`/${PROVIDER_LOCKED_PATH}?from=%2Fsites`);
    expect(lockText(host, '/sites')).toContain('Locked');

    availability().accept(connected('Ready'));
    fixture.detectChanges();

    await router.navigateByUrl('/sites');
    fixture.detectChanges();
    expect(router.url).toBe('/sites');
    expect(lockText(host, '/sites')).not.toContain('Locked');
  });
});

/**
 * The one switchable Admin option in the menu (issue #873): a Super Admin may stop
 * `Historical GST Classification` being offered to everybody else, and keeps it themselves with an
 * `Off` pill beside its name.
 *
 * The answer comes from the capability read - the sidebar decides nothing here either - and it is
 * applied after the capability rules, so it can only ever take away an entry the role already had.
 */
describe('SidebarNavComponent switchable option visibility (issue #873)', () => {
  const ROUTE = '/admin/historical-gst-classification';
  const OFF: OptionVisibility = { historicalGstClassificationVisible: false };
  const ON: OptionVisibility = { historicalGstClassificationVisible: true };

  async function openAdmin(
    optionVisibility: OptionVisibility | undefined,
    options: { diagnosticsAccess?: boolean; rolePreviewActive?: boolean } = {}
  ) {
    const rendered = await render({
      access: of(granted(CAPABILITIES, optionVisibility)),
      diagnosticsAccess: of(options.diagnosticsAccess ?? false),
      rolePreviewActive: options.rolePreviewActive
    });
    groupToggle(rendered.host, 'Admin').click();
    rendered.fixture.detectChanges();
    return rendered;
  }

  it('leaves the entry out for an eligible member while the option is off', async () => {
    const { host } = await openAdmin(OFF);

    expect(link(host, ROUTE)).toBeNull();
    // The rest of the Admin group is untouched: this is one option, not a section.
    expect(link(host, '/admin/costing-repair')).not.toBeNull();
    expect(link(host, '/admin/imports')).not.toBeNull();
  });

  it('offers it, with no pill, while the option is on', async () => {
    const { host } = await openAdmin(ON);

    expect(link(host, ROUTE)).not.toBeNull();
    expect(link(host, ROUTE)?.textContent).not.toContain('Off');
  });

  it('offers it, with no pill, while the setting has not been reported', async () => {
    const { host } = await openAdmin(undefined);

    expect(link(host, ROUTE)).not.toBeNull();
    expect(link(host, ROUTE)?.textContent).not.toContain('Off');
  });

  it('keeps it for a super admin while the option is off, marked Off and still linked', async () => {
    const { host } = await openAdmin(OFF, { diagnosticsAccess: true });

    const entry = link(host, ROUTE);
    expect(entry).not.toBeNull();
    expect(entry?.querySelector('.badge')?.textContent?.trim()).toBe('Off');
    expect(entry?.textContent).toContain('hidden from everybody but a Super Admin');
    expect(entry?.hasAttribute('aria-disabled')).toBe(false);
  });

  it('marks nothing Off for a super admin while the option is on', async () => {
    const { host } = await openAdmin(ON, { diagnosticsAccess: true });

    expect(link(host, ROUTE)?.textContent).not.toContain('Off');
  });

  /**
   * A role preview withholds the administrator's own treatment here exactly as it withholds the
   * `Super Admin` group (issue #721): what the preview shows is what the previewed role sees.
   */
  it('withholds the entry inside a role preview, as it does the Super Admin group', async () => {
    const { host } = await openAdmin(OFF, { diagnosticsAccess: true, rolePreviewActive: true });

    expect(link(host, ROUTE)).toBeNull();
  });

  it('never offers it to a role without Data.Repair, whichever state it is in', async () => {
    const manager = [
      'Dashboard.View',
      'Dashboard.ViewFinancials',
      'PickList.View',
      'Inventory.Operate',
      'Purchasing.Operate',
      'Expenses.Manage',
      'Reports.View',
      'Imports.Run',
      'SiteCommissions.Manage'
    ] as const;

    const { host, fixture } = await render({
      access: of(granted(manager, ON)),
      diagnosticsAccess: of(true)
    });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, ROUTE)).toBeNull();
  });
});
