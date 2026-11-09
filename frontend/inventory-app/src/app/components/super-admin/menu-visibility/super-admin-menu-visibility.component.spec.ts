import { TestBed } from '@angular/core/testing';
import { NEVER, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { MENU_VISIBILITY_KEYS } from '../../../access/option-visibility';
import { MenuVisibilityOption, MenuVisibilityService } from '../../../services/menu-visibility.service';
import { SuperAdminMenuVisibilityComponent } from './super-admin-menu-visibility.component';

/**
 * The catalogue as the API reports it: every key, grouped and in its own order, with its own group
 * and label - the Reports group of issue #1153 above the Admin one.
 */
const CATALOGUE: readonly MenuVisibilityOption[] = [
  { key: 'reports.bookkeeping', group: 'Reports', label: 'Bookkeeping', visible: true },
  { key: 'reports.daily', group: 'Reports', label: 'Daily', visible: true },
  { key: 'reports.transactions', group: 'Reports', label: 'Transactions', visible: true },
  { key: 'reports.reconciliation', group: 'Reports', label: 'Reconciliation', visible: true },
  { key: 'reports.machines', group: 'Reports', label: 'Machines', visible: true },
  { key: 'reports.products', group: 'Reports', label: 'Products', visible: false },
  { key: 'reports.site-commissions', group: 'Reports', label: 'Site Commissions', visible: true },
  { key: 'reports.gst', group: 'Reports', label: 'GST', visible: true },
  { key: 'admin.site-commission-agreements', group: 'Admin', label: 'Site Commission Agreements', visible: true },
  { key: 'admin.imports', group: 'Admin', label: 'Imports', visible: true },
  { key: 'admin.historical-cost-recovery', group: 'Admin', label: 'Historical Cost Recovery', visible: true },
  { key: 'admin.avco-transition', group: 'Admin', label: 'AVCO Transition', visible: true },
  { key: 'admin.costing-repair', group: 'Admin', label: 'Costing Repair', visible: true },
  { key: 'admin.historical-gst-classification', group: 'Admin', label: 'Historical GST Classification', visible: false },
  { key: 'admin.nayax-sale-timestamp-repair', group: 'Admin', label: 'Nayax Sale Timestamp Repair', visible: true }
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

/** The section headings in the order they are rendered, which must be the server's own order. */
function sectionTitles(host: HTMLElement): string[] {
  return Array.from(host.querySelectorAll('.card-title')).map((title) => title.textContent?.trim() ?? '');
}

/** The keys listed under one section heading, so "above" and "under" are both assertable. */
function rowsUnder(host: HTMLElement, group: string): string[] {
  const section = Array.from(host.querySelectorAll('section.card')).find(
    (candidate) => candidate.querySelector('.card-title')?.textContent?.trim() === group
  );
  expect(section).toBeDefined();
  return Array.from(section!.querySelectorAll('[data-testid^="row-"]')).map((row) =>
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
  it('lists every key the API reports, in catalogue order, under its own section', async () => {
    const list = jest.fn(() => of({ options: CATALOGUE }));
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(listed(host)).toEqual([...MENU_VISIBILITY_KEYS]);
    expect(host.textContent).toContain('Admin');
    expect(rowText(host, 'admin.costing-repair')).toContain('Costing Repair');
  });

  /**
   * The Reports section is above the Admin one (issue #1153), and it is above it because the server
   * reported it first: the page groups the options it was given and invents no order of its own.
   */
  it('shows a Reports section above the Admin section, each holding its own rows', async () => {
    const { host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })) });

    expect(sectionTitles(host)).toEqual(['Reports', 'Admin']);
    expect(rowsUnder(host, 'Reports')).toEqual([
      'reports.bookkeeping',
      'reports.daily',
      'reports.transactions',
      'reports.reconciliation',
      'reports.machines',
      'reports.products',
      'reports.site-commissions',
      'reports.gst'
    ]);
    expect(rowsUnder(host, 'Admin')).toEqual([
      'admin.site-commission-agreements',
      'admin.imports',
      'admin.historical-cost-recovery',
      'admin.avco-transition',
      'admin.costing-repair',
      'admin.historical-gst-classification',
      'admin.nayax-sale-timestamp-repair'
    ]);
    expect(rowText(host, 'reports.gst')).toContain('GST');
  });

  /** A report row is the same row an Admin one is: the same state, pill, control and save. */
  it('switches a report off and on with the same control and the server’s own answer', async () => {
    const set = jest.fn((key: string, visible: boolean) => of({ key, group: 'Reports', label: 'Daily', visible }));
    const refresh = jest.fn(() => of({ status: 'unread' as const }));
    const { fixture, host } = await render({ list: jest.fn(() => of({ options: CATALOGUE })), set }, { refresh });

    expect(toggle(host, 'reports.daily').getAttribute('aria-label')).toBe('Daily visibility');
    expect(toggle(host, 'reports.products').getAttribute('aria-checked')).toBe('false');
    expect(rowText(host, 'reports.products')).toContain('Off');

    toggle(host, 'reports.daily').click();
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith('reports.daily', false);
    expect(toggle(host, 'reports.daily').getAttribute('aria-checked')).toBe('false');
    expect(rowText(host, 'reports.daily')).toContain('Off');
    expect(refresh).toHaveBeenCalledTimes(1);
    // One row, one write: the Admin rows beside it are untouched.
    expect(toggle(host, 'admin.imports').getAttribute('aria-checked')).toBe('true');
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
    const set = jest.fn(() => of({ key: 'admin.costing-repair', group: 'Admin', label: 'Costing Repair', visible: false }));
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
      of({
        key: 'admin.historical-gst-classification',
        group: 'Admin',
        label: 'Historical GST Classification',
        visible: true
      })
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
        : of({ key, group: 'Admin', label: 'Imports', visible: false })
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
      fail ? throwError(() => new Error('refused')) : of({ key, group: 'Admin', label: 'Costing Repair', visible })
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
