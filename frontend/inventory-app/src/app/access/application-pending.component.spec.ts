import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { ApplicationPendingComponent } from './application-pending.component';
import { AccessService } from './access.service';
import { AccountStateAnswer, AccountStateName, AccountStateService } from './account-state.service';

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}
@Component({ standalone: true, template: 'rejected' })
class RejectedPageComponent {}
@Component({ standalone: true, template: 'welcome' })
class WelcomePageComponent {}
@Component({ standalone: true, template: 'unavailable' })
class UnavailablePageComponent {}

function known(state: AccountStateName): AccountStateAnswer {
  return { status: 'known', accountState: { state, onboardingEnabled: false, rejectionReason: null } };
}

const UNAVAILABLE: AccountStateAnswer = { status: 'unavailable' };

interface Harness {
  fixture: ComponentFixture<ApplicationPendingComponent>;
  router: Router;
  accountStateRefresh: jest.Mock;
  accessRefresh: jest.Mock;
}

async function createHarness(accountStateRefreshResult: AccountStateAnswer): Promise<Harness> {
  const accountStateRefresh = jest.fn(() => of(accountStateRefreshResult));
  const accessRefresh = jest.fn(() => of({ status: 'granted' }));

  await TestBed.configureTestingModule({
    imports: [ApplicationPendingComponent],
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'welcome', component: WelcomePageComponent },
        { path: 'application-rejected', component: RejectedPageComponent },
        { path: 'access-unavailable', component: UnavailablePageComponent }
      ]),
      { provide: AccountStateService, useValue: { refresh: accountStateRefresh } },
      { provide: AccessService, useValue: { refresh: accessRefresh } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ApplicationPendingComponent);
  return { fixture, router: TestBed.inject(Router), accountStateRefresh, accessRefresh };
}

/**
 * The pending-approval page (issue #542, one slice of #508 under #328). The "check again" button
 * re-reads the account state and acts on whatever it now reports.
 */
describe('ApplicationPendingComponent (issue #542)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('explains that the Super Admin will review the application', async () => {
    const { fixture } = await createHarness(known('ApplicationPending'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Super Admin');
  });

  it('stays on the page and reports still pending when the re-read is still ApplicationPending', async () => {
    const { fixture } = await createHarness(known('ApplicationPending'));
    fixture.detectChanges();

    fixture.componentInstance.checkAgain();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Still pending');
    expect(fixture.componentInstance.checking).toBe(false);
  });

  it('refreshes the access cache and loads the app once approved', async () => {
    const { fixture, router, accountStateRefresh, accessRefresh } = await createHarness(known('Member'));
    fixture.detectChanges();

    fixture.componentInstance.checkAgain();
    await fixture.whenStable();

    expect(accountStateRefresh).toHaveBeenCalled();
    expect(accessRefresh).toHaveBeenCalled();
    expect(router.url).toBe('/');
  });

  it('refreshes the account state before the access cache', async () => {
    const calls: string[] = [];
    const { fixture } = await createHarness(known('Member'));
    const component = fixture.componentInstance;
    (TestBed.inject(AccountStateService).refresh as jest.Mock).mockImplementation(() => {
      calls.push('account-state');
      return of(known('Member'));
    });
    (TestBed.inject(AccessService).refresh as jest.Mock).mockImplementation(() => {
      calls.push('access');
      return of({ status: 'granted' });
    });
    fixture.detectChanges();

    component.checkAgain();
    await fixture.whenStable();

    expect(calls).toEqual(['account-state', 'access']);
  });

  it.each<[AccountStateName, string]>([
    ['NoMembership', '/welcome'],
    ['ApplicationRejected', '/application-rejected'],
    ['MembershipRevoked', '/access-unavailable']
  ])('navigates to %s\'s own page when the re-read reports %s', async (state, expectedPath) => {
    const { fixture, router } = await createHarness(known(state));
    fixture.detectChanges();

    fixture.componentInstance.checkAgain();
    await fixture.whenStable();

    expect(router.url).toBe(expectedPath);
  });

  it('shows a retry message and stays put when the re-read could not be answered', async () => {
    const { fixture, router } = await createHarness(UNAVAILABLE);
    const navigateByUrl = jest.spyOn(router, 'navigateByUrl');
    fixture.detectChanges();

    fixture.componentInstance.checkAgain();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Try again');
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(fixture.componentInstance.checking).toBe(false);
  });

  it('disables the button and shows a checking state while the re-read is in flight', async () => {
    const pending = new Subject<AccountStateAnswer>();
    const { fixture } = await createHarness(known('ApplicationPending'));
    (TestBed.inject(AccountStateService).refresh as jest.Mock).mockReturnValue(pending);
    fixture.detectChanges();

    fixture.componentInstance.checkAgain();

    expect(fixture.componentInstance.checking).toBe(true);

    pending.next(known('ApplicationPending'));
    pending.complete();
    await fixture.whenStable();

    expect(fixture.componentInstance.checking).toBe(false);
  });
});
