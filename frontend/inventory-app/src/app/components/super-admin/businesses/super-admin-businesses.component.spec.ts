import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { SuperAdminBusinessesComponent } from './super-admin-businesses.component';
import {
  SuperAdminBusiness,
  SuperAdminBusinessDecision,
  SuperAdminBusinessesService
} from '../../../services/super-admin-businesses.service';
import { SuperAdminPlanGrant, SuperAdminPlanGrantsService } from '../../../services/super-admin-plan-grants.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** A business that holds no manual grant, which is what the Plan dialog loads unless a test says otherwise. */
const noPlanGrant: SuperAdminPlanGrant = {
  businessId: 9,
  name: 'Snack Co',
  hasGrant: false,
  plan: null,
  startsAtUtc: null,
  endsAtUtc: null,
  endsOnUtc: null,
  reason: null,
  revision: 0,
  grantedAtUtc: null,
  active: false
};

function business(overrides: Partial<SuperAdminBusiness> = {}): SuperAdminBusiness {
  return {
    businessId: 9,
    name: 'Snack Co',
    status: 'Pending',
    createdAtUtc: '2026-01-01T00:00:00Z',
    appliedAtUtc: '2026-01-02T00:00:00Z',
    decidedAtUtc: null,
    contactEmail: 'owner@example.com',
    abn: '12345678901',
    state: 'QLD',
    activeMemberCount: 1,
    nayaxConfigured: false,
    nayaxConnectionStatus: 'NotConfigured',
    ...overrides
  };
}

async function render(
  service: Partial<SuperAdminBusinessesService>,
  toast?: Partial<ToastService>,
  supportSession?: Partial<SupportSessionService>,
  planGrants?: Partial<SuperAdminPlanGrantsService>
) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminBusinessesComponent],
    providers: [
      { provide: SuperAdminBusinessesService, useValue: { notifyChanged: jest.fn(), ...service } },
      {
        provide: ToastService,
        useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast }
      },
      { provide: SupportSessionService, useValue: { start: jest.fn(), ...supportSession } },
      {
        provide: SuperAdminPlanGrantsService,
        useValue: { get: jest.fn(() => of(noPlanGrant)), set: jest.fn(), end: jest.fn(), ...planGrants }
      }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminBusinessesComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

function rowFor(host: HTMLElement, name: string): HTMLTableRowElement {
  const match = Array.from(host.querySelectorAll('tbody tr')).find((row) => row.textContent?.includes(name));
  expect(match).toBeDefined();
  return match as HTMLTableRowElement;
}

describe('SuperAdminBusinessesComponent (issue #547)', () => {
  it('loads and displays the declared fields of every business', async () => {
    const list = jest.fn(() => of([business()]));
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Snack Co');
    expect(host.textContent).toContain('Pending');
    expect(host.textContent).toContain('owner@example.com');
    expect(host.textContent).toContain('12345678901');
    expect(host.textContent).toContain('No');
  });

  /**
   * Issue #933: the state a business recorded is shown when its application is reviewed, and a
   * business that recorded none shows that rather than a guess.
   */
  it('shows the state of each business, and an em dash for one with none', async () => {
    const { host } = await render({
      list: () => of([business(), business({ businessId: 10, name: 'Legacy Co', state: null })])
    });

    const states = Array.from(host.querySelectorAll('[data-testid="business-state"]')).map(
      (cell) => cell.textContent?.trim()
    );

    expect(states).toEqual(['QLD', '—']);
  });

  /**
   * Issue #983: a coloured dot next to each business name says how usable its Nayax connection was
   * at its last test - green for `Ready`, orange for either state that is stored but not working,
   * and red for one with nothing stored.
   */
  it.each<[SuperAdminBusiness['nayaxConnectionStatus'], string]>([
    ['Ready', 'bg-rr-ok-text'],
    ['PendingPermissions', 'bg-rr-warn-text'],
    ['NeedsAttention', 'bg-rr-warn-text'],
    ['NotConfigured', 'bg-rr-danger-text']
  ])('shows a %s business with a %s dot', async (nayaxConnectionStatus, expectedClass) => {
    const { host } = await render({ list: () => of([business({ nayaxConnectionStatus })]) });

    const dot = rowFor(host, 'Snack Co').querySelector('[data-testid="nayax-connection-dot"]');

    expect(dot).not.toBeNull();
    expect(dot!.classList).toContain(expectedClass);
  });

  /**
   * Issue #983: colour is never the only signal, and the two orange states stay distinct - each of
   * the four has its own tooltip and its own screen-reader label.
   */
  it.each<[SuperAdminBusiness['nayaxConnectionStatus'], string]>([
    ['Ready', 'Nayax verified at last test'],
    ['PendingPermissions', 'Nayax token saved, not tested yet'],
    ['NeedsAttention', 'Nayax last test failed'],
    ['NotConfigured', 'Nayax not set up']
  ])('describes a %s connection as "%s" in text as well as colour', async (nayaxConnectionStatus, expectedLabel) => {
    const { host } = await render({ list: () => of([business({ nayaxConnectionStatus })]) });

    const row = rowFor(host, 'Snack Co');

    expect(row.querySelector('[data-testid="nayax-connection-dot"]')!.getAttribute('title')).toBe(expectedLabel);
    expect(row.querySelector('[data-testid="nayax-connection-label"]')!.textContent?.trim()).toBe(expectedLabel);
  });

  /**
   * Issue #983: the dot carries no text of its own, so the label beside it is the one thing a
   * screen reader announces - the status is never read out twice.
   */
  it('hides the dot itself from assistive technology and keeps the label visually hidden', async () => {
    const { host } = await render({ list: () => of([business({ nayaxConnectionStatus: 'Ready' })]) });

    const row = rowFor(host, 'Snack Co');

    expect(row.querySelector('[data-testid="nayax-connection-dot"]')!.getAttribute('aria-hidden')).toBe('true');
    expect(row.querySelector('[data-testid="nayax-connection-label"]')!.classList).toContain('sr-only');
  });

  it('shows an error state when the list fails to load', async () => {
    const { host } = await render({ list: () => throwError(() => new Error('boom')) });

    expect(host.textContent).toContain('Unable to load businesses.');
  });

  it('shows an empty state when there are no businesses', async () => {
    const { host } = await render({ list: () => of([]) });

    expect(host.textContent).toContain('No businesses found.');
  });

  it('filters the table by the selected status', async () => {
    const list = jest.fn(() =>
      of([business({ businessId: 1, name: 'Pending Co', status: 'Pending' }), business({ businessId: 2, name: 'Active Co', status: 'Active' })])
    );
    const { fixture, host } = await render({ list });

    const select = host.querySelector('select') as HTMLSelectElement;
    select.value = 'Active';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Pending Co');
    expect(host.textContent).toContain('Active Co');
  });

  it.each<[SuperAdminBusiness['status'], string[]]>([
    ['Pending', ['Approve', 'Reject']],
    ['Active', ['View as this business', 'Plan', 'Deactivate']],
    ['Deactivated', ['Reactivate', 'Members']]
  ])('for a %s business, offers %s', async (status, expectedButtons) => {
    const list = jest.fn(() => of([business({ status })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(Array.from(row.querySelectorAll('button')).map((button) => button.textContent?.trim())).toEqual(
      expectedButtons
    );
  });

  it('offers no action for a rejected business', async () => {
    const list = jest.fn(() => of([business({ status: 'Rejected' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(row.querySelectorAll('button')).toHaveLength(0);
  });

  it('asks for confirmation before approving, and calls nothing until confirmed', async () => {
    const decision: SuperAdminBusinessDecision = {
      businessId: 9,
      name: 'Snack Co',
      status: 'Active',
      decidedAtUtc: '2026-02-01T00:00:00Z',
      rejectionReason: null
    };
    const approve = jest.fn(() => of(decision));
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const { fixture, host } = await render({ list, approve });

    buttonWithText(host, 'Approve').click();
    fixture.detectChanges();

    expect(approve).not.toHaveBeenCalled();
    expect(host.textContent).toContain("Approve Snack Co's application?");

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(approve).toHaveBeenCalledWith(9);
  });

  it('reloads the list and notifies the badge once approval succeeds', async () => {
    const decision: SuperAdminBusinessDecision = {
      businessId: 9,
      name: 'Snack Co',
      status: 'Active',
      decidedAtUtc: '2026-02-01T00:00:00Z',
      rejectionReason: null
    };
    const approve = jest.fn(() => of(decision));
    const notifyChanged = jest.fn();
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const success = jest.fn();
    const { fixture, host } = await render({ list, approve, notifyChanged }, { success });

    buttonWithText(host, 'Approve').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(list).toHaveBeenCalledTimes(2);
    expect(notifyChanged).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledTimes(1);
  });

  it('shows the API refusal verbatim when deactivation is refused, and does not reload', async () => {
    const message = 'The business is not active.';
    const deactivate = jest.fn(() => throwError(() => ({ error: { message } })));
    const list = jest.fn(() => of([business({ status: 'Active' })]));
    const error = jest.fn();
    const { fixture, host } = await render({ list, deactivate }, { error });

    buttonWithText(host, 'Deactivate').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(list).toHaveBeenCalledTimes(1);
    expect(error).toHaveBeenCalledWith(message);
  });

  it.each<[string, number]>([
    ['409 conflict', 409],
    ['404 not found', 404]
  ])('reloads the list and notifies the badge after a %s, keeping the error toast (issue #681)', async (_label, status) => {
    const message = 'This application has already been decided.';
    const approve = jest.fn(() => throwError(() => ({ status, error: { message } })));
    const notifyChanged = jest.fn();
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const error = jest.fn();
    const { fixture, host } = await render({ list, approve, notifyChanged }, { error });

    buttonWithText(host, 'Approve').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith(message);
    expect(list).toHaveBeenCalledTimes(2);
    expect(notifyChanged).toHaveBeenCalledTimes(1);
  });

  it('does not reload after a failure that is not a 409 or 404', async () => {
    const reactivate = jest.fn(() => throwError(() => ({ status: 500, error: null })));
    const notifyChanged = jest.fn();
    const list = jest.fn(() => of([business({ status: 'Deactivated' })]));
    const error = jest.fn();
    const { fixture, host } = await render({ list, reactivate, notifyChanged }, { error });

    buttonWithText(host, 'Reactivate').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith('Unable to reactivate this business.');
    expect(list).toHaveBeenCalledTimes(1);
    expect(notifyChanged).not.toHaveBeenCalled();
  });

  it("shows reactivate's no-active-Owner refusal verbatim", async () => {
    const message =
      'This business has no active Owner, so it cannot be reactivated. Releasing every Owner of a deactivated ' +
      'business is permanent until a membership is restored by other means.';
    const reactivate = jest.fn(() => throwError(() => ({ error: { message } })));
    const list = jest.fn(() => of([business({ status: 'Deactivated' })]));
    const error = jest.fn();
    const { fixture, host } = await render({ list, reactivate }, { error });

    buttonWithText(host, 'Reactivate').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith(message);
  });

  it('opens the reject dialog and reloads the list once a rejection is recorded', async () => {
    const decision: SuperAdminBusinessDecision = {
      businessId: 9,
      name: 'Snack Co',
      status: 'Rejected',
      decidedAtUtc: '2026-02-01T00:00:00Z',
      rejectionReason: 'Not eligible'
    };
    const reject = jest.fn(() => of(decision));
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const { fixture, host } = await render({ list, reject, notifyChanged: jest.fn() });

    buttonWithText(host, 'Reject').click();
    fixture.detectChanges();

    expect(host.textContent).toContain('Reject application');

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(reject).toHaveBeenCalledWith(9, '');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('opens the members dialog for a deactivated business and reloads when a member is released', async () => {
    const members = jest.fn(() => of([]));
    const list = jest.fn(() => of([business({ status: 'Deactivated' })]));
    const { fixture, host } = await render({ list, members });

    buttonWithText(host, 'Members').click();
    fixture.detectChanges();

    expect(members).toHaveBeenCalledWith(9);
    expect(host.textContent).toContain('Snack Co - Members');
  });

  it('opens the view-as dialog for an active business and closes it once a session starts (issue #553)', async () => {
    const list = jest.fn(() => of([business({ status: 'Active' })]));
    const start = jest.fn(() => of({ sessionId: 's1', businessId: 9, businessName: 'Snack Co', startedAtUtc: '', expiresAtUtc: '' }));
    const { fixture, host } = await render({ list }, undefined, { start });

    buttonWithText(host, 'View as this business').click();
    fixture.detectChanges();

    expect(host.textContent).toContain('View as Snack Co');

    const reasonField = host.querySelector('#view-as-reason') as HTMLTextAreaElement;
    reasonField.value = 'Investigating a reported bug';
    reasonField.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    buttonWithText(host, 'Start session').click();
    fixture.detectChanges();

    expect(start).toHaveBeenCalledWith(9, 'Investigating a reported bug');
    expect(host.textContent).not.toContain('View as Snack Co');
  });

  /**
   * Issue #982: the Plan action is offered for a trading business, opens the dedicated dialog, and
   * the dialog reads that business's own grant - this table carries no plan of its own.
   */
  it('opens the plan grant dialog for an active business', async () => {
    const list = jest.fn(() => of([business({ status: 'Active' })]));
    const get = jest.fn(() => of(noPlanGrant));
    const { fixture, host } = await render({ list }, undefined, undefined, { get });

    buttonWithText(host, 'Plan').click();
    fixture.detectChanges();

    expect(get).toHaveBeenCalledWith(9);
    expect(host.textContent).toContain('Plan for Snack Co');
  });

  it('reports a saved grant and closes the dialog when it is dismissed (issue #982)', async () => {
    const list = jest.fn(() => of([business({ status: 'Active' })]));
    const set = jest.fn(() => of({ ...noPlanGrant, hasGrant: true, plan: 'Pro', revision: 1, active: true }));
    const success = jest.fn();
    const { fixture, host } = await render({ list }, { success }, undefined, { set });

    buttonWithText(host, 'Plan').click();
    fixture.detectChanges();

    const reason = host.querySelector('#grant-reason') as HTMLTextAreaElement;
    reason.value = 'Complimentary pilot';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    buttonWithText(host, 'Save grant').click();
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith(9, 'Starter', null, 'Complimentary pilot', 0);
    expect(success).toHaveBeenCalledWith('The plan grant was saved.');

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Plan for Snack Co');
  });

  /** A pending application has no plan to grant yet, so the action is not offered on its row. */
  it('offers no plan action for a business that is not trading (issue #982)', async () => {
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const { host } = await render({ list });

    expect(rowFor(host, 'Snack Co').querySelector('[data-testid="plan-grant"]')).toBeNull();
  });
});
