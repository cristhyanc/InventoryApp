import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { Observable, of, throwError } from 'rxjs';
import { AccountStateAnswer, AccountStateName, AccountStateService } from './account-state.service';
import {
  BusinessApplicationComponent,
  GENERIC_SUBMIT_ERROR_MESSAGE,
  ONBOARDING_DISABLED_MESSAGE,
  RATE_LIMITED_MESSAGE
} from './business-application.component';
import {
  GST_REGISTRATION_REQUIRED_MESSAGE,
  NAME_REQUIRED_MESSAGE,
  STATE_REQUIRED_MESSAGE
} from './business-application-rules';
import { BusinessApplicationService } from './business-application.service';

@Component({ standalone: true, template: 'apply' })
class ApplyPageComponent {}
@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}
@Component({ standalone: true, template: 'welcome' })
class WelcomePageComponent {}
@Component({ standalone: true, template: 'pending' })
class PendingPageComponent {}
@Component({ standalone: true, template: 'unavailable' })
class UnavailablePageComponent {}

function known(state: AccountStateName, onboardingEnabled = true): AccountStateAnswer {
  return { status: 'known', accountState: { state, onboardingEnabled, rejectionReason: null } };
}

interface SignedInAccount {
  username: string;
  idTokenClaims?: Record<string, unknown>;
}

interface Harness {
  fixture: ComponentFixture<BusinessApplicationComponent>;
  host: HTMLElement;
  router: Router;
  submit: jest.Mock;
  refresh: jest.Mock;
}

const OWNER: SignedInAccount = { username: 'owner@example.com', idTokenClaims: { preferred_username: 'owner@example.com' } };

async function render(options: {
  accountState$?: Observable<AccountStateAnswer>;
  submit?: jest.Mock;
  account?: SignedInAccount | null;
}): Promise<Harness> {
  const submit =
    options.submit ??
    jest.fn(() => of({ name: 'Corner Store', status: 'PendingApproval', appliedAtUtc: '2026-10-13T00:00:00Z' }));
  const refresh = jest.fn(() => of(known('ApplicationPending')));
  const account = options.account === undefined ? OWNER : options.account;

  await TestBed.configureTestingModule({
    imports: [BusinessApplicationComponent],
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'apply', component: ApplyPageComponent },
        { path: 'welcome', component: WelcomePageComponent },
        { path: 'application-pending', component: PendingPageComponent },
        { path: 'access-unavailable', component: UnavailablePageComponent }
      ]),
      {
        provide: AccountStateService,
        useValue: { state$: options.accountState$ ?? of(known('NoMembership')), refresh }
      },
      { provide: BusinessApplicationService, useValue: { submit } },
      {
        provide: MsalService,
        useValue: {
          instance: {
            getActiveAccount: () => account,
            getAllAccounts: () => (account === null ? [] : [account])
          }
        }
      }
    ]
  }).compileComponents();

  const router = TestBed.inject(Router);
  await router.navigateByUrl('/apply');
  const fixture = TestBed.createComponent(BusinessApplicationComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, host: fixture.nativeElement as HTMLElement, router, submit, refresh };
}

/** Fills every field with a valid answer, as a person completing the form would. */
function fillValid(fixture: ComponentFixture<BusinessApplicationComponent>): void {
  const draft = fixture.componentInstance.draft;
  draft.name = ' Corner Store ';
  draft.contactEmail = 'owner@example.com';
  draft.abn = '51 824 753 556';
  draft.gstRegistered = true;
  draft.timeZoneId = 'Australia/Sydney';
  draft.state = 'NSW';
}

async function submitForm(harness: Harness): Promise<void> {
  harness.fixture.componentInstance.submit();
  harness.fixture.detectChanges();
  await harness.fixture.whenStable();
  harness.fixture.detectChanges();
}

function byTestId(host: HTMLElement, id: string): HTMLElement | null {
  return host.querySelector(`[data-testid="${id}"]`);
}

function refusal(status: number, body: unknown): Observable<never> {
  return throwError(() => new HttpErrorResponse({ status, error: body }));
}

/**
 * The business application form at `/apply` (issue #543, one slice of #508 under #328): offered to
 * the account states with no active membership while onboarding is enabled, validated with the
 * API's own rules, and landing on `/application-pending` once the application is recorded.
 */
describe('BusinessApplicationComponent (issue #543)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each<AccountStateName>(['NoMembership', 'MembershipRevoked', 'ApplicationRejected'])(
    'shows the form to an eligible %s caller while onboarding is enabled',
    async (state) => {
      const { host, router } = await render({ accountState$: of(known(state)) });

      expect(host.querySelector('form')).not.toBeNull();
      expect(router.url).toBe('/apply');
    }
  );

  it('shows the form when the account state could not be read, leaving eligibility to the API', async () => {
    const { host } = await render({ accountState$: of({ status: 'unavailable' }) });

    expect(host.querySelector('form')).not.toBeNull();
  });

  it.each<[AccountStateName, string]>([
    ['ApplicationPending', '/application-pending'],
    ['Member', '/'],
    ['BusinessDeactivated', '/access-unavailable'],
    ['MembershipAmbiguous', '/access-unavailable']
  ])('sends an ineligible %s caller to %s instead of showing the form', async (state, destination) => {
    const { host, router } = await render({ accountState$: of(known(state)) });

    expect(router.url).toBe(destination);
    expect(host.querySelector('form')).toBeNull();
  });

  it('sends an eligible caller back to their own page when onboarding is disabled', async () => {
    const { host, router } = await render({ accountState$: of(known('NoMembership', false)) });

    expect(router.url).toBe('/welcome');
    expect(host.querySelector('form')).toBeNull();
  });

  it("starts the contact email as the signed-in account's own address", async () => {
    const { fixture } = await render({});

    expect(fixture.componentInstance.draft.contactEmail).toBe('owner@example.com');
  });

  it('skips a sign-in claim that is not an email address', async () => {
    const { fixture } = await render({
      account: { username: 'live.com#someone', idTokenClaims: { preferred_username: 'live.com#someone', email: 'someone@example.com' } }
    });

    expect(fixture.componentInstance.draft.contactEmail).toBe('someone@example.com');
  });

  it('leaves the contact email blank when no account is signed in', async () => {
    const { fixture } = await render({ account: null });

    expect(fixture.componentInstance.draft.contactEmail).toBe('');
  });

  it('shows no field error until the field is left', async () => {
    const { fixture, host } = await render({});

    expect(byTestId(host, 'error-name')).toBeNull();

    const name = host.querySelector('#businessName') as HTMLInputElement;
    name.dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(byTestId(host, 'error-name')?.textContent).toContain(NAME_REQUIRED_MESSAGE);
    expect(byTestId(host, 'error-gstRegistered')).toBeNull();
  });

  it('shows every field error and sends nothing when an incomplete form is submitted', async () => {
    const harness = await render({});

    await submitForm(harness);

    expect(harness.submit).not.toHaveBeenCalled();
    expect(byTestId(harness.host, 'error-name')?.textContent).toContain(NAME_REQUIRED_MESSAGE);
    expect(byTestId(harness.host, 'error-gstRegistered')?.textContent).toContain(GST_REGISTRATION_REQUIRED_MESSAGE);
  });

  it('submits the normalized request, re-reads the account state and lands on the pending page', async () => {
    const harness = await render({});
    fillValid(harness.fixture);

    await submitForm(harness);

    expect(harness.submit).toHaveBeenCalledWith({
      name: 'Corner Store',
      contactEmail: 'owner@example.com',
      abn: '51824753556',
      gstRegistered: true,
      timeZoneId: 'Australia/Sydney',
      state: 'NSW'
    });
    expect(harness.refresh).toHaveBeenCalledTimes(1);
    expect(harness.router.url).toBe('/application-pending');
  });

  /**
   * Issue #933: the application form asks for the state, offers exactly the eight the API accepts,
   * starts with none chosen - it is never derived from the browser's time zone - and sends nothing
   * until one is.
   */
  it('asks for the state or territory and sends nothing until one is chosen', async () => {
    const harness = await render({});
    fillValid(harness.fixture);
    harness.fixture.componentInstance.draft.state = '';

    await submitForm(harness);

    expect(harness.submit).not.toHaveBeenCalled();
    expect(byTestId(harness.host, 'error-state')?.textContent).toContain(STATE_REQUIRED_MESSAGE);

    const options = Array.from(
      harness.host.querySelectorAll<HTMLOptionElement>('#businessState option')
    ).map((option) => option.value);
    expect(options).toEqual(['', 'NSW', 'VIC', 'QLD', 'SA', 'WA', 'TAS', 'ACT', 'NT']);
  });

  it('starts with no state chosen', async () => {
    const { fixture } = await render({});

    expect(fixture.componentInstance.draft.state).toBe('');
  });

  it('lets a revoked member submit an application', async () => {
    const harness = await render({ accountState$: of(known('MembershipRevoked')) });
    fillValid(harness.fixture);

    await submitForm(harness);

    expect(harness.submit).toHaveBeenCalledTimes(1);
    expect(harness.router.url).toBe('/application-pending');
  });

  it.each<[string, number, unknown, string]>([
    ['a conflict', 409, { message: 'This account already belongs to a business.' }, 'This account already belongs to a business.'],
    ['a rule the form missed', 400, { message: 'An ABN must be 11 digits and pass the ABN check-digit test.' }, 'An ABN must be 11 digits and pass the ABN check-digit test.'],
    ['the rate limit', 429, { detail: 'Wait a few minutes and try again.' }, 'Wait a few minutes and try again.'],
    ['the rate limit without a detail', 429, null, RATE_LIMITED_MESSAGE],
    ['onboarding switched off', 404, null, ONBOARDING_DISABLED_MESSAGE],
    ['a server error', 500, { message: 'Internal detail' }, GENERIC_SUBMIT_ERROR_MESSAGE]
  ])('shows %s and stays on the form', async (_case, status, body, expected) => {
    const harness = await render({ submit: jest.fn(() => refusal(status, body)) });
    fillValid(harness.fixture);

    await submitForm(harness);

    expect(byTestId(harness.host, 'submit-error')?.textContent).toContain(expected);
    expect(harness.refresh).not.toHaveBeenCalled();
    expect(harness.router.url).toBe('/apply');
    expect(harness.fixture.componentInstance.submitting).toBe(false);
  });
});
