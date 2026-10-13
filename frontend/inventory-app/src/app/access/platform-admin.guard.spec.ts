import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { BehaviorSubject, Observable, Subject, of, throwError } from 'rxjs';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { PlatformDiagnosticsAccess, PlatformDiagnosticsService } from '../services/platform-diagnostics.service';
import { platformAdminGuard } from './platform-admin.guard';

@Component({ standalone: true, template: 'diagnostics' })
class DiagnosticsPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

async function withGuard(isGranted: () => Observable<boolean>): Promise<Router> {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'super-admin/diagnostics', component: DiagnosticsPageComponent, canActivate: [platformAdminGuard] }
      ]),
      { provide: PlatformDiagnosticsAccessService, useValue: { isGranted } }
    ]
  }).compileComponents();

  return TestBed.inject(Router);
}

/**
 * `platformAdminGuard` (issue #546): presentation in front of `/super-admin/*`, reusing the same
 * cached probe the sidebar's `Super Admin` group asks. A refusal goes to the dashboard, never to
 * any membership-handling destination - platform-admin authority has nothing to do with business
 * membership or role capability.
 */
describe('platformAdminGuard (issue #546)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lets the configured platform administrator through', async () => {
    const router = await withGuard(() => of(true));

    await router.navigateByUrl('/super-admin/diagnostics');

    expect(router.url).toBe('/super-admin/diagnostics');
  });

  it('sends a refusal to the dashboard, not to any membership-handling destination', async () => {
    const router = await withGuard(() => of(false));

    await router.navigateByUrl('/super-admin/diagnostics');

    expect(router.url).toBe('/');
  });
});

/**
 * The guard asks about whoever is signed in now (issue #605). The real
 * `PlatformDiagnosticsAccessService` is wired up here, with MSAL and the diagnostics call under
 * the test's control: replaying one identity's grant to the next person on the same browser, or
 * refusing the administrator over an answer that has not arrived yet, are both guard-visible
 * failures of the cache rather than of the guard's own two lines.
 */
describe('platformAdminGuard across an identity change (issue #605)', () => {
  const administrator = { name: 'A', homeAccountId: 'a-home-id' } as unknown as AccountInfo;
  const ordinaryMember = { name: 'B', homeAccountId: 'b-home-id' } as unknown as AccountInfo;

  const authorizedResponse: PlatformDiagnosticsAccess = {
    authorized: true,
    crossBusinessScope: true,
    limits: { maxSqlBytes: 16384, maxRows: 500, maxResponseBytes: 1048576, maxDurationSeconds: 5 }
  };

  interface ProbedGuard {
    router: Router;
    switchTo(next: AccountInfo): void;
  }

  /** One diagnostics response per probe, in order; the last one answers every further probe. */
  function queued(...responses: readonly (() => Observable<PlatformDiagnosticsAccess>)[]) {
    let call = 0;
    return () => (responses[call++] ?? responses[responses.length - 1])();
  }

  async function withRealProbe(
    signedIn: AccountInfo,
    access: () => Observable<PlatformDiagnosticsAccess>
  ): Promise<ProbedGuard> {
    let activeAccount = signedIn;
    const events = new Subject<EventMessage>();

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: DashboardPageComponent },
          { path: 'super-admin/diagnostics', component: DiagnosticsPageComponent, canActivate: [platformAdminGuard] }
        ]),
        { provide: PlatformDiagnosticsService, useValue: { access, query: jest.fn() } },
        {
          provide: MsalService,
          useValue: {
            instance: { getActiveAccount: () => activeAccount, getAllAccounts: () => [activeAccount] }
          }
        },
        {
          provide: MsalBroadcastService,
          useValue: {
            inProgress$: new BehaviorSubject<InteractionStatus>(InteractionStatus.None),
            msalSubject$: events
          }
        }
      ]
    }).compileComponents();

    return {
      router: TestBed.inject(Router),
      switchTo(next: AccountInfo): void {
        activeAccount = next;
        events.next({ eventType: EventType.ACTIVE_ACCOUNT_CHANGED } as EventMessage);
      }
    };
  }

  it('refuses the next identity rather than replaying the previous grant', async () => {
    const { router, switchTo } = await withRealProbe(
      administrator,
      queued(
        () => of(authorizedResponse),
        () => throwError(() => ({ status: 403, error: null }))
      )
    );

    await router.navigateByUrl('/super-admin/diagnostics');
    expect(router.url).toBe('/super-admin/diagnostics');

    switchTo(ordinaryMember);
    // Away and back, because the router ignores a navigation to the URL it is already on - and
    // what the next identity does is open the page again, not re-enter the same address.
    await router.navigateByUrl('/');
    await router.navigateByUrl('/super-admin/diagnostics');

    expect(router.url).toBe('/');
  });

  it('waits for the new identity\'s own answer instead of refusing on the invalidated one', async () => {
    const pending = new Subject<PlatformDiagnosticsAccess>();
    const { router, switchTo } = await withRealProbe(
      ordinaryMember,
      queued(
        () => throwError(() => ({ status: 403, error: null })),
        () => pending
      )
    );

    await router.navigateByUrl('/super-admin/diagnostics');
    expect(router.url).toBe('/');

    switchTo(administrator);
    const navigating = router.navigateByUrl('/super-admin/diagnostics');
    pending.next(authorizedResponse);
    await navigating;

    expect(router.url).toBe('/super-admin/diagnostics');
  });
});
