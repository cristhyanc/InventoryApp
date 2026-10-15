import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { SupportSessionBlockedComponent } from './support-session-blocked.component';
import { ToastService } from '../services/toast.service';
import { SupportSessionService, SupportSessionState } from '../support-session/support-session.service';

const UNRESOLVED: SupportSessionState = {
  kind: 'unresolved',
  businessName: 'Snack Co',
  expiresAtUtc: '2026-02-01T01:00:00Z'
};

async function render(supportSession: Partial<SupportSessionService>, toastError = jest.fn()) {
  await TestBed.configureTestingModule({
    imports: [SupportSessionBlockedComponent],
    providers: [
      { provide: SupportSessionService, useValue: supportSession },
      { provide: ToastService, useValue: { error: toastError } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupportSessionBlockedComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('SupportSessionBlockedComponent (issue #668)', () => {
  it('renders nothing while this tab is in ordinary context', async () => {
    const { host } = await render({ state$: of<SupportSessionState>({ kind: 'inactive' }) });

    expect(host.textContent?.trim()).toBe('');
  });

  it('renders nothing while this tab is an effective support view', async () => {
    const { host } = await render({
      state$: of<SupportSessionState>({ kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' })
    });

    expect(host.textContent?.trim()).toBe('');
  });

  it('explains the session this tab cannot present, naming the business, and offers End session', async () => {
    const { host } = await render({ state$: of(UNRESOLVED) });

    const panel = host.querySelector('[data-testid="support-session-unresolved"]');
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain('Snack Co');
    expect(panel?.textContent).toContain('this tab');
    expect(buttonWithText(host, 'End session')).toBeDefined();
  });

  it('never labels anything as being viewed as the business', async () => {
    const { host } = await render({ state$: of(UNRESOLVED) });

    expect(host.textContent).not.toContain('Viewing as');
  });

  it('explains itself even when the business name is unknown', async () => {
    const { host } = await render({ state$: of<SupportSessionState>({ ...UNRESOLVED, businessName: '' }) });

    const panel = host.querySelector('[data-testid="support-session-unresolved"]');
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain('another business');
    expect(buttonWithText(host, 'End session')).toBeDefined();
  });

  it('ends the session, which is what returns this tab to ordinary context', async () => {
    const end = jest.fn(() => of(undefined));
    const { fixture, host } = await render({ state$: of(UNRESOLVED), end });

    buttonWithText(host, 'End session').click();
    fixture.detectChanges();

    expect(end).toHaveBeenCalledTimes(1);
  });

  it('shows the API refusal, re-enables the action and keeps the explanation when ending fails', async () => {
    const end = jest.fn(() => throwError(() => ({ error: { message: 'Unable to reach the server.' } })));
    const toastError = jest.fn();
    const { fixture, host } = await render({ state$: of(UNRESOLVED), end }, toastError);

    buttonWithText(host, 'End session').click();
    fixture.detectChanges();

    expect(toastError).toHaveBeenCalledWith('Unable to reach the server.');
    expect(buttonWithText(host, 'End session').disabled).toBe(false);
    expect(host.querySelector('[data-testid="support-session-unresolved"]')).not.toBeNull();
  });

  it('disappears once the session is resolved or over, with no stale explanation left showing', async () => {
    const state$ = new Subject<SupportSessionState>();
    const { fixture, host } = await render({ state$ });

    state$.next(UNRESOLVED);
    fixture.detectChanges();
    expect(host.textContent).toContain('Snack Co');

    state$.next({ kind: 'inactive' });
    fixture.detectChanges();
    expect(host.textContent?.trim()).toBe('');
  });
});
