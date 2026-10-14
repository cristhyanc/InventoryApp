import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BusinessRejectDialogComponent } from './business-reject-dialog.component';
import { SuperAdminBusinessDecision, SuperAdminBusinessesService } from '../../../services/super-admin-businesses.service';

const decision: SuperAdminBusinessDecision = {
  businessId: 9,
  name: 'Snack Co',
  status: 'Rejected',
  decidedAtUtc: '2026-02-01T00:00:00Z',
  rejectionReason: 'Not eligible'
};

async function render(service: Partial<SuperAdminBusinessesService>) {
  await TestBed.configureTestingModule({
    imports: [BusinessRejectDialogComponent],
    providers: [{ provide: SuperAdminBusinessesService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessRejectDialogComponent);
  fixture.componentRef.setInput('businessId', 9);
  fixture.componentRef.setInput('businessName', 'Snack Co');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('BusinessRejectDialogComponent (issue #547)', () => {
  it('shows the business name and an optional reason field', async () => {
    const { host } = await render({});

    expect(host.textContent).toContain('Snack Co');
    expect(host.querySelector('textarea')).not.toBeNull();
  });

  it('rejects with the typed reason and emits the decision', async () => {
    const reject = jest.fn(() => of(decision));
    const notifyChanged = jest.fn();
    const { fixture, host, component } = await render({ reject, notifyChanged });
    const rejected = jest.fn();
    component.rejected.subscribe(rejected);

    const textarea = host.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = 'Not eligible';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(reject).toHaveBeenCalledWith(9, 'Not eligible');
    expect(notifyChanged).toHaveBeenCalledTimes(1);
    expect(rejected).toHaveBeenCalledWith(decision);
  });

  it('rejects with an empty reason when none is typed', async () => {
    const reject = jest.fn(() => of(decision));
    const { fixture, host } = await render({ reject, notifyChanged: jest.fn() });

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(reject).toHaveBeenCalledWith(9, '');
  });

  it('shows the API refusal verbatim and does not emit rejected', async () => {
    const message = 'The reason is longer than 500 characters once trimmed.';
    const reject = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host, component } = await render({ reject });
    const rejected = jest.fn();
    component.rejected.subscribe(rejected);

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(host.textContent).toContain(message);
    expect(rejected).not.toHaveBeenCalled();
  });

  it('emits canceled without calling the API', async () => {
    const reject = jest.fn();
    const { fixture, host, component } = await render({ reject });
    const canceled = jest.fn();
    component.canceled.subscribe(canceled);

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(reject).not.toHaveBeenCalled();
    expect(canceled).toHaveBeenCalledTimes(1);
  });
});
