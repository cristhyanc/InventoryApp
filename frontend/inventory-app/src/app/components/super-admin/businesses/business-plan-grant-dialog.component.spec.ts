import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BusinessPlanGrantDialogComponent } from './business-plan-grant-dialog.component';
import { SuperAdminPlanGrant, SuperAdminPlanGrantsService } from '../../../services/super-admin-plan-grants.service';

const noGrant: SuperAdminPlanGrant = {
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

const proGrant: SuperAdminPlanGrant = {
  businessId: 9,
  name: 'Snack Co',
  hasGrant: true,
  plan: 'Pro',
  startsAtUtc: '2026-10-29T13:45:00Z',
  endsAtUtc: '2027-01-01T00:00:00Z',
  endsOnUtc: '2026-12-31',
  reason: 'Complimentary pilot',
  revision: 4,
  grantedAtUtc: '2026-10-29T13:45:00Z',
  active: true
};

async function render(service: Partial<SuperAdminPlanGrantsService>) {
  await TestBed.configureTestingModule({
    imports: [BusinessPlanGrantDialogComponent],
    providers: [{ provide: SuperAdminPlanGrantsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessPlanGrantDialogComponent);
  fixture.componentRef.setInput('businessId', 9);
  fixture.componentRef.setInput('businessName', 'Snack Co');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function click(host: HTMLElement, testId: string): void {
  const button = host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement | null;
  expect(button).not.toBeNull();
  button!.click();
}

function type(host: HTMLElement, selector: string, value: string, fixture: { detectChanges: () => void }): void {
  const field = host.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('BusinessPlanGrantDialogComponent (issue #982)', () => {
  it('loads the business’s current grant when it opens', async () => {
    const get = jest.fn(() => of(noGrant));
    const { host } = await render({ get });

    expect(get).toHaveBeenCalledWith(9);
    expect(host.textContent).toContain('Snack Co');
    expect(host.querySelector('[data-testid="current-grant"]')?.textContent).toContain('no manual grant');
  });

  /** A business with no grant has nothing to end, so the destructive action is not offered. */
  it('offers no End grant action for a business that holds none', async () => {
    const { host } = await render({ get: jest.fn(() => of(noGrant)) });

    expect(host.querySelector('[data-testid="end-grant"]')).toBeNull();
  });

  it('shows the existing grant, its end date and its reason, and offers to end it', async () => {
    const { host, component } = await render({ get: jest.fn(() => of(proGrant)) });

    expect(host.querySelector('[data-testid="current-grant"]')?.textContent).toContain('Granted Pro');
    expect(component.plan).toBe('Pro');
    expect(component.endsOn).toBe('2026-12-31');
    expect(component.reason).toBe('Complimentary pilot');
    expect(host.querySelector('[data-testid="end-grant"]')).not.toBeNull();
  });

  it('saves the chosen plan, end date and reason with the revision the server reported', async () => {
    const set = jest.fn(() => of({ ...proGrant, revision: 5 }));
    const { fixture, host, component } = await render({ get: jest.fn(() => of(proGrant)), set });
    const saved = jest.fn();
    component.saved.subscribe(saved);

    type(host, '#grant-ends-on', '2027-06-30', fixture);
    type(host, '#grant-reason', 'Extended', fixture);
    click(host, 'save-grant');
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith(9, 'Pro', '2027-06-30', 'Extended', 4);
    expect(saved).toHaveBeenCalledTimes(1);
  });

  /** An empty date field is "no end date", not an empty string the server has to interpret. */
  it('sends no end date when the field is left empty', async () => {
    const set = jest.fn(() => of(noGrant));
    const { fixture, host } = await render({ get: jest.fn(() => of(noGrant)), set });

    type(host, '#grant-reason', 'Pilot', fixture);
    click(host, 'save-grant');
    fixture.detectChanges();

    expect(set).toHaveBeenCalledWith(9, 'Starter', null, 'Pilot', 0);
  });

  it('ends the grant with the typed reason and the reported revision', async () => {
    const end = jest.fn(() => of({ ...proGrant, active: false, revision: 5 }));
    const { fixture, host, component } = await render({ get: jest.fn(() => of(proGrant)), end });
    const saved = jest.fn();
    component.saved.subscribe(saved);

    type(host, '#grant-reason', 'Pilot finished', fixture);
    click(host, 'end-grant');
    fixture.detectChanges();

    expect(end).toHaveBeenCalledWith(9, 'Pilot finished', 4);
    expect(saved).toHaveBeenCalledTimes(1);
    expect(host.querySelector('[data-testid="end-grant"]')).toBeNull();
  });

  /**
   * The concurrency refusal, as a Super Admin sees it: the server's own sentence, shown verbatim,
   * and nothing emitted - the dialog never retries with a guessed revision.
   */
  it('shows the server’s stale-revision refusal verbatim and emits nothing', async () => {
    const message = 'This business’s plan grant has changed since it was read. Reload it and decide again.';
    const set = jest.fn(() => throwError(() => ({ status: 409, error: { message } })));
    const { fixture, host, component } = await render({ get: jest.fn(() => of(proGrant)), set });
    const saved = jest.fn();
    component.saved.subscribe(saved);

    click(host, 'save-grant');
    fixture.detectChanges();

    expect(host.textContent).toContain(message);
    expect(saved).not.toHaveBeenCalled();
  });

  it('shows a load failure instead of an editable form', async () => {
    const { host } = await render({ get: jest.fn(() => throwError(() => ({ status: 500 }))) });

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Unable to load');
    expect(host.querySelector('[data-testid="save-grant"]')).toBeNull();
  });

  it('closes without calling the API', async () => {
    const set = jest.fn();
    const { fixture, host, component } = await render({ get: jest.fn(() => of(noGrant)), set });
    const closed = jest.fn();
    component.closed.subscribe(closed);

    const cancel = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Cancel');
    cancel!.click();
    fixture.detectChanges();

    expect(set).not.toHaveBeenCalled();
    expect(closed).toHaveBeenCalledTimes(1);
  });
});
