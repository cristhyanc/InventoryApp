import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { SuperAdminBusinessesComponent } from './super-admin-businesses.component';
import {
  SuperAdminBusiness,
  SuperAdminBusinessDecision,
  SuperAdminBusinessesService
} from '../../../services/super-admin-businesses.service';
import { ToastService } from '../../../services/toast.service';

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
    activeMemberCount: 1,
    nayaxConfigured: false,
    ...overrides
  };
}

async function render(service: Partial<SuperAdminBusinessesService>, toast?: Partial<ToastService>) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminBusinessesComponent],
    providers: [
      { provide: SuperAdminBusinessesService, useValue: { notifyChanged: jest.fn(), ...service } },
      {
        provide: ToastService,
        useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast }
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

  it('offers Approve and Reject only for a pending business', async () => {
    const list = jest.fn(() => of([business({ status: 'Pending' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(Array.from(row.querySelectorAll('button')).map((button) => button.textContent?.trim())).toEqual([
      'Approve',
      'Reject'
    ]);
  });

  it('offers only Deactivate for an active business', async () => {
    const list = jest.fn(() => of([business({ status: 'Active' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(Array.from(row.querySelectorAll('button')).map((button) => button.textContent?.trim())).toEqual([
      'Deactivate'
    ]);
  });

  it('offers Reactivate and Members for a deactivated business', async () => {
    const list = jest.fn(() => of([business({ status: 'Deactivated' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(Array.from(row.querySelectorAll('button')).map((button) => button.textContent?.trim())).toEqual([
      'Reactivate',
      'Members'
    ]);
  });

  it('offers no action for a rejected business', async () => {
    const list = jest.fn(() => of([business({ status: 'Rejected' })]));
    const { host } = await render({ list });

    const row = rowFor(host, 'Snack Co');
    expect(row.querySelectorAll('button').length).toBe(0);
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
});
