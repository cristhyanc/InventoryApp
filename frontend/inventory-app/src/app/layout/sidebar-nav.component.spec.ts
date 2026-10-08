import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { ICON_PATHS, OUTLINED_ICON_SHAPES } from '../components/shared/icon-paths';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { PRIMARY_NAVIGATION_ID, SidebarNavComponent } from './sidebar-nav.component';
import { NavGroup, navLinks, primaryNavigation } from './navigation';

@Component({ standalone: true, template: '' })
class BlankPageComponent {}

interface Rendered {
  fixture: ComponentFixture<SidebarNavComponent>;
  component: SidebarNavComponent;
  host: HTMLElement;
}

/**
 * The diagnostics access probe is stubbed, because the sidebar must never be the thing that
 * decides platform-admin access: it renders whatever the API's answer was (issue #335).
 */
async function render(
  options: {
    expanded?: boolean;
    drawer?: boolean;
    url?: string;
    navId?: string;
    diagnosticsAccess?: Observable<boolean>;
  } = {}
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [SidebarNavComponent],
    providers: [
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      {
        provide: PlatformDiagnosticsAccessService,
        useValue: { isGranted: () => options.diagnosticsAccess ?? of(false) }
      }
    ]
  }).compileComponents();

  if (options.url) {
    await TestBed.inject(Router).navigateByUrl(options.url);
  }

  const fixture = TestBed.createComponent(SidebarNavComponent);
  fixture.componentRef.setInput('expanded', options.expanded ?? true);
  fixture.componentRef.setInput('drawer', options.drawer ?? false);
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

const adminChildRoutes = navGroup('Admin').children.map((child) => child.route);

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
  });

  it('keeps every group collapsed until its heading is used, and exposes aria-expanded on it', async () => {
    const { host } = await render();

    for (const label of ['Products', 'Purchases', 'Reports', 'Admin']) {
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
    for (const label of ['Products', 'Purchases', 'Reports', 'Admin']) {
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
    expect(link(host, '/reports')?.getAttribute('aria-current')).toBeNull();
  });

  it('keeps the parent group heading active while one of its pages is open', async () => {
    const { component, host } = await render({ url: '/admin/avco-transition' });

    expect(component.isGroupActive(navGroup('Admin'))).toBe(true);
    expect(component.isGroupActive(navGroup('Reports'))).toBe(false);
    expect(groupToggle(host, 'Admin').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/admin/avco-transition')?.getAttribute('aria-current')).toBe('page');
  });

  it('gives the active item the Material dark-gradient styling hook and its parent heading a visible active hook', async () => {
    const { host } = await render({ url: '/admin/avco-transition' });

    expect(link(host, '/admin/avco-transition')?.classList.contains('bg-md-dark-gradient')).toBe(true);
    expect(link(host, '/admin/avco-transition')?.classList.contains('text-white')).toBe(true);
    expect(link(host, '/admin/historical-cost-recovery')?.classList.contains('bg-md-dark-gradient')).toBe(false);
    expect(groupToggle(host, 'Admin').classList.contains('bg-md-gray-100')).toBe(true);
    expect(groupToggle(host, 'Reports').classList.contains('bg-md-gray-100')).toBe(false);
  });

  it('marks a top-level link active from a detail URL without activating any group', async () => {
    const { host } = await render({ url: '/machines/7' });

    expect(link(host, '/machines')?.getAttribute('aria-current')).toBe('page');
    expect(groupToggle(host, 'Products').getAttribute('aria-expanded')).toBe('false');
    expect(link(host, '/machines')?.classList.contains('bg-md-dark-gradient')).toBe(true);
    expect(link(host, '/')?.classList.contains('bg-md-dark-gradient')).toBe(false);
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
 * The conditional super-admin diagnostics link (issue #335). The sidebar renders the API's
 * answer; it never decides it, and a hidden link protects nothing - `/admin/diagnostics` and both
 * diagnostics endpoints are independently authorized by the API.
 */
describe('SidebarNavComponent super-admin diagnostics link (issue #335)', () => {
  it('offers no diagnostics link when the API does not confirm platform-admin access', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: of(false) });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, '/admin/diagnostics')).toBeNull();
    expect(host.textContent).not.toContain('Platform Diagnostics');
  });

  it('offers no diagnostics link while the access probe has not answered', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: NEVER });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, '/admin/diagnostics')).toBeNull();
  });

  it('offers no diagnostics link when the access probe fails', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: throwError(() => new Error('probe failed')) });
    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    expect(link(host, '/admin/diagnostics')).toBeNull();
  });

  it('renders it inside the Admin group once the API confirms access', async () => {
    const { fixture, host } = await render({ diagnosticsAccess: of(true) });
    expect(link(host, '/admin/diagnostics')).toBeNull();

    groupToggle(host, 'Admin').click();
    fixture.detectChanges();

    const diagnostics = link(host, '/admin/diagnostics');
    expect(diagnostics).not.toBeNull();
    expect(diagnostics?.textContent?.trim()).toBe('Platform Diagnostics');
    for (const route of adminChildRoutes) {
      expect(link(host, route)).not.toBeNull();
    }
  });

  it('marks the diagnostics page active and opens the Admin group for it', async () => {
    const { host } = await render({ url: '/admin/diagnostics', diagnosticsAccess: of(true) });

    expect(groupToggle(host, 'Admin').getAttribute('aria-expanded')).toBe('true');
    expect(link(host, '/admin/diagnostics')?.getAttribute('aria-current')).toBe('page');
  });

  it('adds no other destination when access is confirmed', async () => {
    const { component } = await render({ diagnosticsAccess: of(true) });

    expect(component.navigation.map((entry) => entry.label)).toEqual(primaryNavigation.map((entry) => entry.label));
    expect(navLinks(component.navigation).map((entry) => entry.route)).toEqual([
      ...navLinks(primaryNavigation).map((entry) => entry.route),
      '/admin/diagnostics'
    ]);
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
    for (const label of ['Products', 'Purchases', 'Reports', 'Admin']) {
      groupToggle(host, label).click();
    }
    fixture.detectChanges();

    const declaredIcons = primaryNavigation.flatMap((item) => (item.icon === undefined ? [] : [item.icon]));
    expect(declaredIcons.length).toBe(primaryNavigation.length);

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
    expect(row.textContent).toContain('Inventory Manager');
    expect(row.firstElementChild?.textContent).toContain('Inventory Manager');
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

  it('reports every navigation so the shell can dismiss a narrow drawer', async () => {
    const { fixture, component, host } = await render({ drawer: true });
    const navigated = jest.fn();
    component.navigated.subscribe(navigated);

    link(host, '/pick-list')?.click();
    fixture.detectChanges();
    expect(navigated).toHaveBeenCalledTimes(1);

    groupToggle(host, 'Products').click();
    fixture.detectChanges();
    link(host, '/categories')?.click();

    expect(navigated).toHaveBeenCalledTimes(2);
  });
});
