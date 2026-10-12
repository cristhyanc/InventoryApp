import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AccountInfo, EventMessage, EventType, InteractionStatus } from '@azure/msal-browser';
import { Subject, of, throwError } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { AccessNotAllowedComponent } from './access-not-allowed.component';
import { AccessService } from './access.service';
import { CAPABILITIES } from './capabilities';
import { ACCESS_NOT_ALLOWED_PATH, requireCapability } from './capability.guard';

@Component({ standalone: true, template: 'reports' })
class ReportsPageComponent {}

@Component({ standalone: true, template: 'admin' })
class AdminPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

function account(homeAccountId: string): AccountInfo {
  return { homeAccountId } as unknown as AccountInfo;
}

/** The Operator and Owner rows of #328's table, written out rather than derived. */
const OPERATOR_CAPABILITIES = ['Dashboard.View', 'PickList.View', 'Inventory.Operate', 'Purchasing.Operate'];

function response(capabilities: readonly string[]): unknown {
  return { role: 'Test', capabilities: [...capabilities], businessName: 'Vending Co', timeZoneId: 'Australia/Sydney' };
}

interface Guarded {
  router: Router;
  access: AccessService;
  /** Makes MSAL report a different signed-in account, as switching Microsoft account does. */
  switchAccount(accountId: string): void;
}

/**
 * The guard is exercised against the real `AccessService`, with only its one `GET` doubled: a stub
 * that re-implemented the capability answer would be testing itself.
 */
async function withGuards(read: () => unknown): Promise<Guarded> {
  let signedIn = 'member-1';
  const msalSubject$ = new Subject<EventMessage>();
  const access = new AccessService(
    { get: read } as unknown as HttpClient,
    { apiBaseUrl: '/api' } as unknown as ConfigService,
    {
      instance: {
        getActiveAccount: () => account(signedIn),
        getAllAccounts: () => [account(signedIn)]
      }
    } as unknown as MsalService,
    { inProgress$: of(InteractionStatus.None), msalSubject$ } as unknown as MsalBroadcastService
  );

  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent, canActivate: [requireCapability('Dashboard.View')] },
        { path: 'reports', component: ReportsPageComponent, canActivate: [requireCapability('Reports.View')] },
        {
          path: 'admin',
          component: AdminPageComponent,
          canActivate: [requireCapability('Imports.Run', 'Data.Repair')]
        },
        { path: ACCESS_NOT_ALLOWED_PATH, component: AccessNotAllowedComponent }
      ]),
      { provide: AccessService, useValue: access }
    ]
  }).compileComponents();

  return {
    router: TestBed.inject(Router),
    access,
    switchAccount: (accountId) => {
      signedIn = accountId;
      msalSubject$.next({ eventType: EventType.ACTIVE_ACCOUNT_CHANGED } as EventMessage);
    }
  };
}

async function renderAccessNotAllowed(): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [AccessNotAllowedComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(AccessNotAllowedComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/**
 * Route guards are presentation only (issue #528): they replace a page full of refusals with one
 * sentence that explains them. The endpoints behind each page check the same capability
 * themselves, so nothing is protected by a guard and nothing may be moved behind one.
 */
describe('requireCapability (issue #528)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lets a member whose role grants the capability through', async () => {
    const { router } = await withGuards(() => of(response(CAPABILITIES)));

    await router.navigateByUrl('/reports');

    expect(router.url).toBe('/reports');
  });

  it('sends a member whose role does not grant it to the access-not-allowed page', async () => {
    const { router } = await withGuards(() => of(response(OPERATOR_CAPABILITIES)));

    await router.navigateByUrl('/reports');

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });

  it('leaves a page every role holds reachable', async () => {
    const { router } = await withGuards(() => of(response(OPERATOR_CAPABILITIES)));

    await router.navigateByUrl('/');

    expect(router.url).toBe('/');
  });

  it('allows a page declared with several capabilities to anybody holding one of them', async () => {
    const { router } = await withGuards(() => of(response(['Dashboard.View', 'Imports.Run'])));

    await router.navigateByUrl('/admin');

    expect(router.url).toBe('/admin');
  });

  it('refuses that page only when the role holds none of them', async () => {
    const { router } = await withGuards(() => of(response(OPERATOR_CAPABILITIES)));

    await router.navigateByUrl('/admin');

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });

  /**
   * The fail-open rule. The API refuses what the role may not do whatever the router did, so a
   * capability set that could not be read must not lock a member out of their own page. Which
   * screen a person with no usable membership is shown at all is issue #529's work.
   */
  it('lets the route through when the capability set could not be read', async () => {
    const { router } = await withGuards(() => throwError(() => new HttpErrorResponse({ status: 500 })));

    await router.navigateByUrl('/reports');

    expect(router.url).toBe('/reports');
  });

  it('waits for the capability set rather than deciding from an unread one', async () => {
    const pending = new Subject<unknown>();
    const { router, access } = await withGuards(() => pending);
    // The read is in flight before the navigation starts, which is the real sequence: the shell's
    // sidebar asks for the capabilities as the application renders.
    access.access$.subscribe();

    const navigation = router.navigateByUrl('/reports');
    pending.next(response(OPERATOR_CAPABILITIES));
    pending.complete();

    await navigation;
    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });

  it('holds the navigation while the capability set never answers, rather than guessing', async () => {
    const { router } = await withGuards(() => new Subject<unknown>());
    let settled = false;

    router.navigateByUrl('/reports').then(() => (settled = true));
    await Promise.resolve();

    expect(settled).toBe(false);
    expect(router.url).toBe('/');
  });

  /**
   * The guard decides a navigation once. A refresh that arrives afterwards re-renders the menu and
   * the hidden actions; it does not re-evaluate a page the member is already on, which the API
   * keeps authorizing on every request anyway.
   */
  it('decides once, from the answer the navigation was made with', async () => {
    let capabilities: readonly string[] = CAPABILITIES;
    const { router, access } = await withGuards(() => of(response(capabilities)));
    await router.navigateByUrl('/reports');

    capabilities = OPERATOR_CAPABILITIES;
    access.refresh();

    expect(router.url).toBe('/reports');
  });

  /**
   * A guard that activates while the new account's read is still in flight must wait for it
   * (issue #603). The previous account's capability set is not a usable approximation: it would
   * open a page to somebody whose own role may not hold it, and the moment between the switch and
   * the new answer is exactly when a navigation happens.
   */
  it('waits for the new account rather than deciding from the previous account’s capabilities', async () => {
    const pending = new Subject<unknown>();
    const reads: (() => unknown)[] = [() => of(response(CAPABILITIES)), () => pending];
    const { router, access, switchAccount } = await withGuards(() => (reads.shift() as () => unknown)());
    // The shell reads the first account's capabilities as the application renders.
    access.access$.subscribe();

    switchAccount('member-2');
    let settled = false;
    const navigation = router.navigateByUrl('/reports').then(() => (settled = true));
    await Promise.resolve();
    expect(settled).toBe(false);
    expect(router.url).toBe('/');

    pending.next(response(OPERATOR_CAPABILITIES));
    pending.complete();
    await navigation;

    expect(router.url).toBe(`/${ACCESS_NOT_ALLOWED_PATH}`);
  });

  it('lets the new account through once their own capability set allows the page', async () => {
    const pending = new Subject<unknown>();
    const reads: (() => unknown)[] = [() => of(response(OPERATOR_CAPABILITIES)), () => pending];
    const { router, access, switchAccount } = await withGuards(() => (reads.shift() as () => unknown)());
    access.access$.subscribe();

    switchAccount('member-2');
    const navigation = router.navigateByUrl('/reports');
    pending.next(response(CAPABILITIES));
    pending.complete();
    await navigation;

    expect(router.url).toBe('/reports');
  });

  it('reads the capabilities once for several guarded navigations', async () => {
    const read = jest.fn(() => of(response(CAPABILITIES)));
    const { router } = await withGuards(read);

    await router.navigateByUrl('/reports');
    await router.navigateByUrl('/admin');
    await router.navigateByUrl('/');

    expect(read).toHaveBeenCalledTimes(1);
  });
});

describe('AccessNotAllowedComponent (issue #528)', () => {
  afterEach(() => TestBed.resetTestingModule());

  /**
   * The page must teach no part of the role matrix, for the same reason the API's refusal is a
   * bare `403`: a refusal names neither the capability, the role, nor the roles that would have
   * been allowed.
   */
  it('explains the refusal without naming a capability, a role or the role matrix', async () => {
    const text = (await renderAccessNotAllowed()).textContent ?? '';

    expect(text).toContain("You don't have access to this");
    for (const capability of CAPABILITIES) {
      expect(text).not.toContain(capability);
    }
    expect(text).not.toContain('Operator');
    expect(text).not.toContain('Manager');
  });

  it('offers the Dashboard, so the page is never a dead end', async () => {
    const host = await renderAccessNotAllowed();

    expect(host.querySelector('a[href="/"]')).not.toBeNull();
  });
});
