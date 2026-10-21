import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../../access/access.service';
import { CAPABILITIES, Capability, capabilitySet } from '../../access/capabilities';
import { AdminComponent } from './admin.component';

function granted(capabilities: readonly Capability[]): MemberAccessState {
  const access: MemberAccess = {
    role: 'Test',
    capabilities: capabilitySet([...capabilities]),
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney'
  };
  return { status: 'granted', access };
}

/** Every capability, which is what an Owner holds and what most of these tests assume by default. */
const OWNER = granted(CAPABILITIES);

const MANAGER = granted([
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

const OPERATOR = granted(['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate']);

async function render(access: Observable<MemberAccessState> = of(OWNER)) {
  await TestBed.configureTestingModule({
    imports: [AdminComponent],
    providers: [provideRouter([]), { provide: AccessService, useValue: { access$: access } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(AdminComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

/**
 * `[routerLink]` is a property binding here (issue #674's cards are data-driven), so the rendered
 * `href` is what proves a card's link exists - the same way `SidebarNavComponent`'s own spec reads
 * its dynamic links - rather than the static `routerLink="..."` attribute the old literal template
 * left behind.
 */
function link(host: HTMLElement, route: string): HTMLAnchorElement | null {
  return host.querySelector<HTMLAnchorElement>(`a[href="${route}"]`);
}

const ALL_ADMIN_ROUTES = [
  '/admin/nayax-settings',
  '/admin/site-commission-agreements',
  '/admin/imports',
  '/admin/historical-cost-recovery',
  '/admin/avco-transition',
  '/admin/costing-repair',
  '/admin/historical-gst-classification',
  '/admin/nayax-sale-timestamp-repair',
  '/admin/users',
  '/admin/roles',
  '/admin/business-settings'
];

describe('AdminComponent Admin split (issue #388)', () => {
  it('links to the dedicated Integrations and Site Commission Agreements pages instead of embedding their forms', async () => {
    const { host } = await render();

    expect(link(host, '/admin/nayax-settings')).not.toBeNull();
    expect(link(host, '/admin/site-commission-agreements')).not.toBeNull();
    expect(host.querySelector('input[type="date"]')).toBeNull();
  });
});

describe('AdminComponent Admin split (issue #389)', () => {
  it('links to the dedicated Imports page instead of hosting the import actions', async () => {
    const { host } = await render();

    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(host.querySelector('input[type="file"]')).toBeNull();
    const buttonText = Array.from(host.querySelectorAll('button')).map((button) => button.textContent ?? '');
    expect(buttonText.some((text) => text.includes('Import products'))).toBe(false);
    expect(buttonText.some((text) => text.includes('Import XML files'))).toBe(false);
    expect(buttonText.some((text) => text.includes('Download template'))).toBe(false);
  });
});

describe('AdminComponent Historical GST Classification (issue #433)', () => {
  it('links to the dedicated Historical GST Classification page', async () => {
    const { host } = await render();

    expect(link(host, '/admin/historical-gst-classification')).not.toBeNull();
  });
});

describe('AdminComponent Nayax Sale Timestamp Repair (issue #487)', () => {
  it('links to the dedicated Nayax Sale Timestamp Repair page', async () => {
    const { host } = await render();

    expect(link(host, '/admin/nayax-sale-timestamp-repair')).not.toBeNull();
  });
});

describe('AdminComponent Admin split (issue #390)', () => {
  it('links to the dedicated Historical Cost Recovery, AVCO Transition and Costing Repair pages', async () => {
    const { host } = await render();

    expect(link(host, '/admin/historical-cost-recovery')).not.toBeNull();
    expect(link(host, '/admin/avco-transition')).not.toBeNull();
    expect(link(host, '/admin/costing-repair')).not.toBeNull();
  });

  it('keeps no second copy of the moved maintenance tools: no action, form control or child workflow remains', async () => {
    const { host } = await render();

    expect(host.querySelectorAll('button')).toHaveLength(0);
    expect(host.querySelectorAll('input, select, textarea')).toHaveLength(0);
    expect(host.querySelector('app-costing-repair')).toBeNull();
    const text = host.textContent ?? '';
    expect(text).not.toContain('Dry Run');
    expect(text).not.toContain('Apply Nayax Cost Backfill');
    expect(text).not.toContain('Preview transition baseline');
    expect(text).not.toContain('Preview repair');
  });

  /**
   * The page is a link hub only, so it needs no data-access dependency of its own beyond the
   * cached capability read: rendering it with no HTTP, MSAL or maintenance-service provider proves
   * it cannot reach a costing or import service.
   */
  it('renders without any maintenance service dependency', async () => {
    await expect(render()).resolves.toBeDefined();
  });
});

/**
 * The Admin hub now completes the round trip the sidebar's `Admin` group already offers an Owner
 * (issue #674): Users, Roles and Business Settings were reachable from the sidebar and each page's
 * own "Back to Admin" link, but had no card here at all.
 */
describe('AdminComponent Owner-only destinations (issue #674)', () => {
  it('links to the Users, Roles and Business Settings pages', async () => {
    const { host } = await render();

    expect(link(host, '/admin/users')).not.toBeNull();
    expect(link(host, '/admin/roles')).not.toBeNull();
    expect(link(host, '/admin/business-settings')).not.toBeNull();
  });
});

/**
 * The capability-driven hub (issue #674). The hub renders the capability set `AccessService`
 * reports and decides nothing itself - a card it does not render hides a page rather than protects
 * one, because the endpoint behind that page authorizes every request regardless.
 */
describe('AdminComponent capability-driven cards (issue #674)', () => {
  it('renders no card at all until the capability set has been read', async () => {
    const { host } = await render(NEVER);

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).toBeNull();
    }
    expect(host.textContent).not.toContain('Integrations');
  });

  it('offers an Owner every destination the sidebar offers', async () => {
    const { host } = await render(of(OWNER));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).not.toBeNull();
    }
  });

  it('offers a Manager only Imports and Site Commission Agreements, without any Owner-only card', async () => {
    const { host } = await render(of(MANAGER));

    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/site-commission-agreements')).not.toBeNull();

    expect(link(host, '/admin/nayax-settings')).toBeNull();
    expect(link(host, '/admin/historical-cost-recovery')).toBeNull();
    expect(link(host, '/admin/avco-transition')).toBeNull();
    expect(link(host, '/admin/costing-repair')).toBeNull();
    expect(link(host, '/admin/historical-gst-classification')).toBeNull();
    expect(link(host, '/admin/nayax-sale-timestamp-repair')).toBeNull();
    expect(link(host, '/admin/users')).toBeNull();
    expect(link(host, '/admin/roles')).toBeNull();
    expect(link(host, '/admin/business-settings')).toBeNull();
  });

  it('offers an Operator no admin card at all', async () => {
    const { host } = await render(of(OPERATOR));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).toBeNull();
    }
  });

  it('offers every destination when the capability set could not be read, the same fail-open rule the sidebar follows', async () => {
    const { host } = await render(of<MemberAccessState>({ status: 'unavailable' }));

    for (const route of ALL_ADMIN_ROUTES) {
      expect(link(host, route)).not.toBeNull();
    }
  });

  it('re-renders when the capability set changes, so a role change and an identity change both reach the hub', async () => {
    const access = new BehaviorSubject<MemberAccessState>(OPERATOR);
    const { fixture, host } = await render(access);
    expect(link(host, '/admin/business-settings')).toBeNull();

    access.next(OWNER);
    fixture.detectChanges();
    expect(link(host, '/admin/business-settings')).not.toBeNull();

    // An identity change republishes `unread` before the new identity's own answer arrives
    // (issue #603); the hub must withdraw every card for that window rather than keep the
    // previous member's, exactly as the sidebar does.
    access.next({ status: 'unread' });
    fixture.detectChanges();
    expect(link(host, '/admin/business-settings')).toBeNull();
    expect(link(host, '/admin/imports')).toBeNull();

    access.next(MANAGER);
    fixture.detectChanges();
    expect(link(host, '/admin/imports')).not.toBeNull();
    expect(link(host, '/admin/business-settings')).toBeNull();
  });
});
