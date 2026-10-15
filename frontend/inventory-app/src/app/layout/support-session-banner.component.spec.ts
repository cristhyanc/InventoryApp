import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { SupportSessionBannerComponent } from './support-session-banner.component';
import { ToastService } from '../services/toast.service';
import { SupportSessionService, SupportSessionState } from '../support-session/support-session.service';

async function render(supportSession: Partial<SupportSessionService>, toastError = jest.fn()) {
  await TestBed.configureTestingModule({
    imports: [SupportSessionBannerComponent],
    providers: [
      { provide: SupportSessionService, useValue: supportSession },
      { provide: ToastService, useValue: { error: toastError } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupportSessionBannerComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('SupportSessionBannerComponent (issue #553)', () => {
  it('renders nothing while no session is active', async () => {
    const { host } = await render({ state$: of<SupportSessionState>({ kind: 'inactive' }) });

    expect(host.textContent?.trim()).toBe('');
  });

  /**
   * Issue #668: a tab that holds no session id would send its requests as the administrator
   * themselves, so this banner's claim would be false for whatever such a page showed.
   * `SupportSessionBlockedComponent` is what that tab renders instead.
   */
  it('shows no "Viewing as" banner for a session this tab cannot present (issue #668)', async () => {
    const { host } = await render({
      state$: of<SupportSessionState>({
        kind: 'unresolved',
        businessName: 'Snack Co',
        expiresAtUtc: '2026-02-01T01:00:00Z'
      })
    });

    expect(host.textContent?.trim()).toBe('');
  });

  it('shows the business, read-only and the expiry, with an End session action', async () => {
    const { host } = await render({
      state$: of<SupportSessionState>({ kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' })
    });

    expect(host.textContent).toContain('Viewing as Snack Co');
    expect(host.textContent).toContain('read-only');
    expect(buttonWithText(host, 'End session')).toBeDefined();
  });

  it('ends the session from the banner', async () => {
    const end = jest.fn(() => of(undefined));
    const { fixture, host } = await render({
      state$: of<SupportSessionState>({ kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }),
      end
    });

    buttonWithText(host, 'End session').click();
    fixture.detectChanges();

    expect(end).toHaveBeenCalledTimes(1);
  });

  it('shows the API refusal and re-enables the action when ending fails', async () => {
    const end = jest.fn(() => throwError(() => ({ error: { message: 'Unable to reach the server.' } })));
    const toastError = jest.fn();
    const { fixture, host } = await render(
      {
        state$: of<SupportSessionState>({ kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' }),
        end
      },
      toastError
    );

    buttonWithText(host, 'End session').click();
    fixture.detectChanges();

    expect(toastError).toHaveBeenCalledWith('Unable to reach the server.');
    expect(buttonWithText(host, 'End session').disabled).toBe(false);
  });

  it('follows the state as it changes, with no stale banner left showing', async () => {
    const state$ = new Subject<SupportSessionState>();
    const { fixture, host } = await render({ state$ });

    state$.next({ kind: 'viewing', businessName: 'Snack Co', expiresAtUtc: '2026-02-01T01:00:00Z' });
    fixture.detectChanges();
    expect(host.textContent).toContain('Snack Co');

    state$.next({ kind: 'inactive' });
    fixture.detectChanges();
    expect(host.textContent?.trim()).toBe('');
  });
});
