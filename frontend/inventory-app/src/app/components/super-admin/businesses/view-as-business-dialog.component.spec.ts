import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ViewAsBusinessDialogComponent } from './view-as-business-dialog.component';
import { SupportSessionService } from '../../../support-session/support-session.service';

async function render(supportSession: Partial<SupportSessionService>) {
  await TestBed.configureTestingModule({
    imports: [ViewAsBusinessDialogComponent],
    providers: [{ provide: SupportSessionService, useValue: supportSession }]
  }).compileComponents();

  const fixture = TestBed.createComponent(ViewAsBusinessDialogComponent);
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

function typeReason(host: HTMLElement, fixture: { detectChanges(): void }, reason: string): void {
  const textarea = host.querySelector('#view-as-reason') as HTMLTextAreaElement;
  textarea.value = reason;
  textarea.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

describe('ViewAsBusinessDialogComponent (issue #553)', () => {
  it('shows the business name and requires a reason before it can be submitted', async () => {
    const { fixture, host } = await render({});

    expect(host.textContent).toContain('Snack Co');
    expect(buttonWithText(host, 'Start session').disabled).toBe(true);

    typeReason(host, fixture, '   ');
    expect(buttonWithText(host, 'Start session').disabled).toBe(true);

    typeReason(host, fixture, 'Investigating a reported bug');
    expect(buttonWithText(host, 'Start session').disabled).toBe(false);
  });

  it('starts a session with the trimmed reason and emits started', async () => {
    const start = jest.fn(() => of({ sessionId: 's1', businessId: 9, businessName: 'Snack Co', startedAtUtc: '', expiresAtUtc: '' }));
    const { fixture, host, component } = await render({ start });
    const started = jest.fn();
    component.started.subscribe(started);

    typeReason(host, fixture, '  Investigating a reported bug  ');
    buttonWithText(host, 'Start session').click();
    fixture.detectChanges();

    expect(start).toHaveBeenCalledWith(9, 'Investigating a reported bug');
    expect(started).toHaveBeenCalledTimes(1);
  });

  it('shows the API refusal verbatim and does not emit started', async () => {
    const message = 'The reason is longer than 500 characters once trimmed.';
    const start = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host, component } = await render({ start });
    const started = jest.fn();
    component.started.subscribe(started);

    typeReason(host, fixture, 'Investigating a reported bug');
    buttonWithText(host, 'Start session').click();
    fixture.detectChanges();

    expect(host.textContent).toContain(message);
    expect(started).not.toHaveBeenCalled();
  });

  it('emits canceled without calling the API', async () => {
    const start = jest.fn();
    const { fixture, host, component } = await render({ start });
    const canceled = jest.fn();
    component.canceled.subscribe(canceled);

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(start).not.toHaveBeenCalled();
    expect(canceled).toHaveBeenCalledTimes(1);
  });
});
