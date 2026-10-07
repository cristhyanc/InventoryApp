import { TestBed } from '@angular/core/testing';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, firstValueFrom, of, throwError } from 'rxjs';
import { PlatformDiagnosticsAccessService } from './platform-diagnostics-access.service';
import { PlatformDiagnosticsAccess, PlatformDiagnosticsService } from './platform-diagnostics.service';

const account = { name: 'Platform Admin', username: 'admin@example.test' } as unknown as AccountInfo;

const authorizedResponse: PlatformDiagnosticsAccess = {
  authorized: true,
  crossBusinessScope: true,
  limits: { maxSqlBytes: 16384, maxRows: 500, maxResponseBytes: 1048576, maxDurationSeconds: 5 }
};

function refusal(status: number): unknown {
  return { status, error: null };
}

interface Harness {
  service: PlatformDiagnosticsAccessService;
  accessFn: jest.Mock;
  interaction: BehaviorSubject<InteractionStatus>;
}

function configure(
  options: { signedIn?: boolean; access?: () => unknown; interaction?: InteractionStatus } = {}
): Harness {
  const signedIn = options.signedIn ?? true;
  const accessFn = jest.fn(options.access ?? (() => of(authorizedResponse)));
  const interaction = new BehaviorSubject<InteractionStatus>(options.interaction ?? InteractionStatus.None);

  TestBed.configureTestingModule({
    providers: [
      PlatformDiagnosticsAccessService,
      { provide: PlatformDiagnosticsService, useValue: { access: accessFn, query: jest.fn() } },
      {
        provide: MsalService,
        useValue: {
          instance: {
            getActiveAccount: () => (signedIn ? account : null),
            getAllAccounts: () => (signedIn ? [account] : [])
          }
        }
      },
      { provide: MsalBroadcastService, useValue: { inProgress$: interaction } }
    ]
  });

  return { service: TestBed.inject(PlatformDiagnosticsAccessService), accessFn, interaction };
}

/**
 * The diagnostics API is the only source of this answer (issue #335). There is deliberately no
 * frontend role, claim or flag that can grant it, because a second source could only diverge from
 * the configured Entra `(tid, oid)` identity the API resolves - and the page and both endpoints
 * stay independently authorized whatever this service answers.
 */
describe('PlatformDiagnosticsAccessService (issue #335)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('grants the link only when the API confirms access', async () => {
    const { service } = configure();

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);
  });

  it('refuses when the API refuses the capability probe', async () => {
    const { service } = configure({ access: () => throwError(() => refusal(403)) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  it('refuses when the probe fails for any other reason, rather than assuming access', async () => {
    const { service } = configure({ access: () => throwError(() => new Error('network down')) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  /**
   * A 200 body always carries `authorized: true` - a refusal is a 403, not a body - so a body
   * saying otherwise is treated as a refusal rather than trusted.
   */
  it('refuses a 200 body that does not actually confirm access', async () => {
    const { service } = configure({ access: () => of({ ...authorizedResponse, authorized: false }) });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
  });

  it('asks nothing at all when nobody is signed in', async () => {
    const { service, accessFn } = configure({ signedIn: false });

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(false);
    expect(accessFn).not.toHaveBeenCalled();
  });

  /**
   * A protected request issued while MSAL is still handling a redirect would have
   * `MsalInterceptor` start an interactive sign-in of its own, on the public `/auth` landing
   * route, for a menu entry nobody has asked for yet.
   */
  it('waits for MSAL to finish any sign-in interaction before probing', async () => {
    const { service, accessFn, interaction } = configure({ interaction: InteractionStatus.HandleRedirect });
    const granted = firstValueFrom(service.isGranted());
    expect(accessFn).not.toHaveBeenCalled();

    interaction.next(InteractionStatus.None);

    await expect(granted).resolves.toBe(true);
    expect(accessFn).toHaveBeenCalledTimes(1);
  });

  it('probes once however many times the answer is asked for', async () => {
    const { service, accessFn } = configure();

    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);
    await expect(firstValueFrom(service.isGranted())).resolves.toBe(true);

    expect(accessFn).toHaveBeenCalledTimes(1);
  });
});
