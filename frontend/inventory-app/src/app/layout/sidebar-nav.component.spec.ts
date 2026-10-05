import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SidebarNavComponent } from './sidebar-nav.component';
import { NavGroup, navLinks, primaryNavigation } from './navigation';

@Component({ standalone: true, template: '' })
class BlankPageComponent {}

interface Rendered {
  fixture: ComponentFixture<SidebarNavComponent>;
  component: SidebarNavComponent;
  host: HTMLElement;
}

async function render(options: { expanded?: boolean; drawer?: boolean; url?: string } = {}): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [SidebarNavComponent],
    providers: [provideRouter([{ path: '**', component: BlankPageComponent }])]
  }).compileComponents();

  if (options.url) {
    await TestBed.inject(Router).navigateByUrl(options.url);
  }

  const fixture = TestBed.createComponent(SidebarNavComponent);
  fixture.componentRef.setInput('expanded', options.expanded ?? true);
  fixture.componentRef.setInput('drawer', options.drawer ?? false);
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

  it('marks a top-level link active from a detail URL without activating any group', async () => {
    const { host } = await render({ url: '/machines/7' });

    expect(link(host, '/machines')?.getAttribute('aria-current')).toBe('page');
    expect(groupToggle(host, 'Products').getAttribute('aria-expanded')).toBe('false');
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
