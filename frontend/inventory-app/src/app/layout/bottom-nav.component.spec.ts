import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { AccessService, MemberAccess, MemberAccessState } from '../access/access.service';
import { BusinessNavigationVisibility } from '../access/business-navigation';
import { CAPABILITIES, Capability, capabilitySet } from '../access/capabilities';
import { MENU_VISIBILITY_KEYS, OptionVisibility } from '../access/option-visibility';
import { SpecConnectionStatus, provideNayaxConnectionStatus } from '../provider/provider-availability.spec-helpers';
import { BottomNavComponent, shortcutsOf } from './bottom-nav.component';
import { navigationFor } from './navigation';

@Component({ standalone: true, template: '' })
class BlankPageComponent {}

function granted(capabilities: readonly Capability[], optionVisibility?: OptionVisibility): MemberAccessState {
  const access: MemberAccess = {
    role: 'Test',
    capabilities: capabilitySet([...capabilities]),
    businessName: 'Vending Co',
    timeZoneId: 'Australia/Sydney',
    optionVisibility
  };
  return { status: 'granted', access };
}

const OPERATOR: readonly Capability[] = ['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate'];

interface Rendered {
  fixture: ComponentFixture<BottomNavComponent>;
  component: BottomNavComponent;
  host: HTMLElement;
}

async function render(
  options: {
    access?: Observable<MemberAccessState>;
    businessNavigation?: BusinessNavigationVisibility;
    providerStatus?: SpecConnectionStatus;
    url?: string;
  } = {}
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [BottomNavComponent],
    providers: [
      provideRouter([{ path: '**', component: BlankPageComponent }]),
      provideNayaxConnectionStatus(options.providerStatus ?? 'Ready'),
      { provide: AccessService, useValue: { access$: options.access ?? of(granted(CAPABILITIES)) } }
    ]
  }).compileComponents();

  if (options.url) {
    await TestBed.inject(Router).navigateByUrl(options.url);
  }

  const fixture = TestBed.createComponent(BottomNavComponent);
  fixture.componentInstance.businessNavigation = options.businessNavigation ?? 'offered';
  fixture.componentInstance.menuId = 'primary-navigation';
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function hrefs(host: HTMLElement): (string | null)[] {
  return Array.from(host.querySelectorAll('nav[aria-label="Shortcuts"] a')).map((anchor) => anchor.getAttribute('href'));
}

/**
 * The narrow layout's bottom navigation (issue #972). Every shortcut is an entry the member's own
 * menu already offers, so the bar can never reach a page the sidebar would not.
 */
describe('BottomNavComponent (issue #972)', () => {
  it('offers Dashboard, Pick List, Sites and Inventory, then More, to an Owner', async () => {
    const { host } = await render();

    expect(hrefs(host)).toEqual(['/', '/pick-list', '/sites', '/products']);
    expect(Array.from(host.querySelectorAll('nav li')).map((entry) => entry.textContent?.trim())).toEqual([
      'Dashboard',
      'Pick List',
      'Sites',
      'Inventory',
      'More'
    ]);
  });

  it('offers only the shortcuts a role is offered in its menu', async () => {
    const { host } = await render({ access: of(granted(['Reports.View'])) });

    expect(hrefs(host)).toEqual([]);
    expect(host.querySelector('[data-testid="bottom-nav-more"]')).not.toBeNull();
  });

  it('offers no business shortcut while the business menu is withheld', async () => {
    const { host } = await render({ businessNavigation: 'withheld' });

    expect(hrefs(host)).toEqual([]);
    expect(host.querySelector('[data-testid="bottom-nav-more"]')).not.toBeNull();
  });

  it('renders no shortcut until the capability set has been read', async () => {
    const { host } = await render({ access: of({ status: 'unread' } as MemberAccessState) });

    expect(hrefs(host)).toEqual([]);
  });

  it('marks a locked Sites shortcut with the menu own lock sentence, and keeps the link', async () => {
    const { host } = await render({ providerStatus: 'NotConfigured' });
    const sites = host.querySelector('a[href="/sites"]');

    expect(sites).not.toBeNull();
    expect(sites?.querySelector('.sr-only')?.textContent).toContain('Sites');
    expect(host.querySelector('a[href="/"] .sr-only')).toBeNull();
  });

  it('marks the active shortcut, with a group shortcut active for any of its pages', async () => {
    const { host } = await render({ url: '/stock-history' });

    expect(host.querySelector('a[href="/products"]')?.getAttribute('aria-current')).toBe('page');
    expect(host.querySelector('a[href="/"]')?.getAttribute('aria-current')).toBeNull();
  });

  it('reports More with the control itself, so the shell can return focus to it', async () => {
    const { fixture, component, host } = await render();
    const requested = jest.fn();
    component.moreRequested.subscribe(requested);
    const more = host.querySelector<HTMLButtonElement>('[data-testid="bottom-nav-more"]');

    more?.click();
    expect(requested).toHaveBeenCalledWith(more);

    fixture.componentRef.setInput('moreOpen', true);
    fixture.detectChanges();
    expect(more?.getAttribute('aria-expanded')).toBe('true');
    expect(more?.getAttribute('aria-controls')).toBe('primary-navigation');
  });

  it('picks its shortcuts out of navigationFor rather than declaring any of its own', () => {
    expect(shortcutsOf(navigationFor(capabilitySet([...OPERATOR]), false, 'offered')).map((entry) => entry.route)).toEqual([
      '/',
      '/pick-list',
      '/sites',
      '/products'
    ]);
    expect(shortcutsOf(navigationFor(capabilitySet(['Expenses.Manage']), false, 'offered'))).toEqual([]);
  });

  /**
   * The switchable Admin destinations (issue #1151) reach this bar through the same
   * `navigationFor` answer the sidebar uses, and the bar's own shortcuts are four destinations no
   * switch can reach: none of them declares a visibility key, which is asserted here so that a
   * future shortcut that *did* declare one could not quietly stay on the bar after being switched
   * off. The whole menu on a narrow screen is the More sheet, which is the sidebar in drawer mode
   * and is covered by its own spec.
   */
  describe('switchable menu visibility (issue #1151)', () => {
    const everyKeyOff = { switchedOffKeys: [...MENU_VISIBILITY_KEYS] };

    it('offers the same shortcuts whatever is switched off, because no shortcut is switchable', async () => {
      const { host } = await render({ access: of(granted(CAPABILITIES, everyKeyOff)) });

      expect(hrefs(host)).toEqual(['/', '/pick-list', '/sites', '/products']);
    });

    it('declares no visibility key on any shortcut it can offer', () => {
      const sources = shortcutsOf(
        navigationFor(capabilitySet([...CAPABILITIES]), false, 'offered', undefined, { switchedOffKeys: [] })
      ).map((shortcut) => shortcut.source);

      sources.forEach((source) => {
        const keys =
          source.kind === 'group'
            ? source.children.map((child) => (child.kind === 'link' ? child.visibilityKey : undefined))
            : [source.visibilityKey];
        expect(keys.filter((key) => key !== undefined)).toEqual([]);
      });
    });

    it('builds the bar from the switched-off answer the shared read carried', async () => {
      const { component } = await render({ access: of(granted(CAPABILITIES, everyKeyOff)) });

      // The bar's own navigation is the filtered one: no switchable Admin destination survives it,
      // so nothing in it could become a shortcut while those keys are off.
      expect(shortcutsOf(navigationFor(capabilitySet([...CAPABILITIES]), false, 'offered', undefined, everyKeyOff))).toEqual(
        component.shortcuts
      );
    });
  });
});
