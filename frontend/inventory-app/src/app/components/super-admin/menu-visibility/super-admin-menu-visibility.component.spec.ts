import { TestBed } from '@angular/core/testing';
import { NEVER, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { MENU_VISIBILITY_KEYS } from '../../../access/option-visibility';
import { MenuVisibilityOption, MenuVisibilityService } from '../../../services/menu-visibility.service';
import { SuperAdminMenuVisibilityComponent } from './super-admin-menu-visibility.component';

/** The catalogue as the API reports it: every key, in its own order, with its own label. */
const CATALOGUE: readonly MenuVisibilityOption[] = [
  { key: 'admin.site-commission-agreements', label: 'Site Commission Agreements', visible: true },
  { key: 'admin.imports', label: 'Imports', visible: true },
  { key: 'admin.historical-cost-recovery', label: 'Historical Cost Recovery', visible: true },
  { key: 'admin.avco-transition', label: 'AVCO Transition', visible: true },
  { key: 'admin.costing-repair', label: 'Costing Repair', visible: true },
  { key: 'admin.historical-gst-classification', label: 'Historical GST Classification', visible: false },
  { key: 'admin.nayax-sale-timestamp-repair', label: 'Nayax Sale Timestamp Repair', visible: true }
];

async function render(
  service: Partial<MenuVisibilityService>,
  access: Partial<AccessService> = { refresh: jest.fn(() => of({ status: 'unread' as const })) }
) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminMenuVisibilityComponent],
    providers: [
      { provide: MenuVisibilityService, useValue: service },
      { provide: AccessService, useValue: access }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminMenuVisibilityComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function toggle(host: HTMLElement, key: string): HTMLButtonElement {
  const control = host.querySelector(`[data-testid="toggle-${key}"]`) as HTMLButtonElement | null;
  expect(control).not.toBeNull();
  return control!;
}

function rowText(host: HTMLElement, key: string): string {
  return host.querySelector(`[data-testid="row-${key}"]`)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function listed(host: HTMLElement): string[] {
  return Array.from(host.querySelectorAll('[data-testid^="row-"]')).map((row) =>
    (row as HTMLElement).dataset['testid']!.replace('row-', '')
  );
}

/**
 * The Super Admin Menu Visibility page (issue #1151): every switchable Admin destination, with the
 * one control that switches it, and nothing else.
 *
 * It decides nothing. The keys, their labels and their order are the API's answer; every save is the
 * server's own answer about what was stored, and a refused one changes nothing on screen. The page
 * is reachable only through `platformAdminGuard` and is never itself switchable.
 */
describe('SuperAdminMenuVisibilityComponent (issue #1151)', () => {
  it('lists every key the API reports, in catalogue order, under an Admin section', async () => {
    const list = jest.fn(() => of({ options: CATALOGUE }));
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(listed(host)).toEqual([...MENU_VISIBILITY_KEYS]);
    expect(host.textContent).toContain('Admin');
    expect(rowText(host, 'admin.costing-repair')).toContain('Costing Repair');
  });

  /**
   * Every row is listed whatever this business's plan, provider or capabilities are: the page is
   * about the platform's own setting, so it asks nothing about a business and reads nothing of one.
   */
  it('reads nothing but the catalogue, so no plan, provider or capability answer can change the list', async () => {
    const refresh = jest.fn(() => of({ status: 'unread' as const }));
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) }, { refresh });

    expect(listed(host)).toHaveLength(MENU_VISIBILITY_KEYS.length);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('shows each destination in the state the server reported', async () => {
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) });

    expect(toggle(host, 'admin.costing-repair').getAttribute('aria-checked')).toBe('true');
    expect(toggle(host, 'admin.historical-gst-classification').getAttribute('aria-checked')).toBe('false');
    expect(rowText(host, 'admin.historical-gst-classification')).toContain('Off');
  });

  /**
   * The accessible name names the destination, so a screen-reader user hears which switch they are
   * on, and the control is a plain button: keyboard operable without anything added, and carrying no
   * state of its own that a refused save could leave disagreeing with the server.
   */
  it('gives every control an accessible name naming its destination, operable by keyboard', async () => {
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) });

    const control = toggle(host, 'admin.costing-repair');
    expect(control.getAttribute('aria-label')).toBe('Costing Repair visibility');
    expect(control.getAttribute('role')).toBe('switch');
    expect(control.tagName).toBe('BUTTON');
    expect(control.getAttribute('type')).toBe('button');
    expect(control.hasAttribute('disabled')).toBe(false);
    expect(toggle(host, 'admin.imports').getAttribute('aria-label')).toBe('Imports visibility');
  });

  it('switches one destination off and shows the answer the server stored', async () => {
    const set = jest.fn(() => of({ key: 'admin.costing-repair', label: 'Costing Repair', visible: false }));
    const refresh = jest.fn(() => of({ status: 'unread' as const }));
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set }, { refresh });

    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith('admin.costing-repair', false);
    expect(toggle(host, 'admin.costing-repair').getAttribute('aria-checked')).toBe('false');
    expect(rowText(host, 'admin.costing-repair')).toContain('Off');
    // The access read is taken again, so the sidebar, the bottom nav and the Admin hub re-render.
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  /** The opposite of the server's own answer, never of a value this page assumed. */
  it('switches a stored Off destination back on', async () => {
    const set = jest.fn(() =>
      of({ key: 'admin.historical-gst-classification', label: 'Historical GST Classification', visible: true })
    );
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set });

    toggle(host, 'admin.historical-gst-classification').click();
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith('admin.historical-gst-classification', true);
    expect(toggle(host, 'admin.historical-gst-classification').getAttribute('aria-checked')).toBe('true');
  });

  it('keeps the last confirmed state and says the change was not saved when the save fails', async () => {
    const set = jest.fn(() => throwError(() => new Error('refused')));
    const refresh = jest.fn(() => of({ status: 'unread' as const }));
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set }, { refresh });

    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();

    expect(toggle(host, 'admin.costing-repair').getAttribute('aria-checked')).toBe('true');
    const error = host.querySelector('[data-testid="error-admin.costing-repair"]');
    expect(error?.textContent).toContain('not saved');
    expect(error?.getAttribute('role')).toBe('alert');
    // Nothing was stored, so there is nothing for the other surfaces to re-read.
    expect(refresh).not.toHaveBeenCalled();
  });

  it('leaves every other row untouched when one row fails', async () => {
    const set = jest.fn((key: string) =>
      key === 'admin.costing-repair'
        ? throwError(() => new Error('refused'))
        : of({ key, label: 'Imports', visible: false })
    );
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set });

    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();
    toggle(host, 'admin.imports').click();
    fixture.detectChanges();

    expect(toggle(host, 'admin.costing-repair').getAttribute('aria-checked')).toBe('true');
    expect(host.querySelector('[data-testid="error-admin.costing-repair"]')).not.toBeNull();
    expect(toggle(host, 'admin.imports').getAttribute('aria-checked')).toBe('false');
    expect(host.querySelector('[data-testid="error-admin.imports"]')).toBeNull();
  });

  /** A row whose save succeeded stops reporting the failure it had before. */
  it('clears a row’s error once a later save on it succeeds', async () => {
    let fail = true;
    const set = jest.fn((key: string, visible: boolean) =>
      fail ? throwError(() => new Error('refused')) : of({ key, label: 'Costing Repair', visible })
    );
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set });

    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="error-admin.costing-repair"]')).not.toBeNull();

    fail = false;
    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="error-admin.costing-repair"]')).toBeNull();
    expect(toggle(host, 'admin.costing-repair').getAttribute('aria-checked')).toBe('false');
  });

  /** One click, one write: a second one while the first is in flight submits nothing. */
  it('submits nothing while that row’s save is still in flight', async () => {
    const set = jest.fn(() => NEVER);
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set });

    toggle(host, 'admin.costing-repair').click();
    fixture.detectChanges();
    toggle(host, 'admin.costing-repair').click();

    expect(set).toHaveBeenCalledTimes(1);
    expect(toggle(host, 'admin.costing-repair').hasAttribute('disabled')).toBe(true);
  });

  /**
   * What one switch covers is read from the declared menu entries, so a key two entries share says
   * so. With only `Imports` declaring `admin.imports` today there is nothing extra to say; the
   * Reimbursement Report of #1152 declares the same key and the sentence appears with it.
   */
  it('says which other destinations a shared switch covers, and nothing when it covers one', async () => {
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) });

    expect(host.querySelector('[data-testid="covers-admin.costing-repair"]')).toBeNull();
    const imports = host.querySelector('[data-testid="covers-admin.imports"]');
    if (imports !== null) {
      expect(imports.textContent).toContain('Reimbursement Report');
    }
  });

  it('reports a catalogue read that failed, and reads it again when asked', async () => {
    let fail = true;
    const list = jest.fn(() => (fail ? throwError(() => new Error('refused')) : of({ options: CATALOGUE })));
    const { fixture, host } = await render({ list });

    expect(host.querySelector('[data-testid="load-failed"]')?.textContent).toContain('could not be read');
    expect(listed(host)).toEqual([]);

    fail = false;
    (host.querySelector('[data-testid="read-again"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(list).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-testid="load-failed"]')).toBeNull();
    expect(listed(host)).toEqual([...MENU_VISIBILITY_KEYS]);
  });

  it('says it is reading while the catalogue read has not answered', async () => {
    const { host } = await render({ list: jest.fn(() => NEVER) });

    expect(host.querySelector('[data-testid="loading"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="load-failed"]')).toBeNull();
    expect(listed(host)).toEqual([]);
  });

  /** Integrations, Users, Roles and Business Settings are never switchable, so never listed. */
  it('offers no switch for a destination the catalogue does not declare', async () => {
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) });

    const text = host.textContent ?? '';
    expect(text).not.toContain('Integrations');
    expect(text).not.toContain('Users');
    expect(text).not.toContain('Roles');
    expect(text).not.toContain('Business Settings');
  });
});
