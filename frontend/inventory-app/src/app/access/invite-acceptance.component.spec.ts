import { HttpErrorResponse } from '@angular/common/http';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from './access.service';
import { AccountStateAnswer, AccountStateName, AccountStateService } from './account-state.service';
import { ACCOUNT_STATE_ROUTE } from './account-state.guard';
import { InviteAcceptanceComponent } from './invite-acceptance.component';
import { InviteRedemptionService } from './invite-redemption.service';
import { PENDING_INVITE_TOKEN_KEY, storePendingInviteToken } from './pending-invite-token';

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}
@Component({ standalone: true, template: 'welcome' })
class WelcomePageComponent {}
@Component({ standalone: true, template: 'pending' })
class PendingPageComponent {}
@Component({ standalone: true, template: 'rejected' })
class RejectedPageComponent {}
@Component({ standalone: true, template: 'unavailable' })
class UnavailablePageComponent {}
@Component({ standalone: true, template: 'reopen' })
class ReopenPageComponent {}

/**
 * The version the account state publishes in these tests. Deliberately *not* the version in force:
 * all this page does with it is carry it back unchanged, and a value that happened to equal the
 * real constant would pass whether it was carried back or re-derived here.
 */
const LEGAL_VERSION = '2020-01-02';

function known(state: AccountStateName, legalVersion: string | null = LEGAL_VERSION): AccountStateAnswer {
  return {
    status: 'known',
    accountState: { state, onboardingEnabled: false, rejectionReason: null, legalVersion }
  };
}

const UNAVAILABLE: AccountStateAnswer = { status: 'unavailable' };

/**
 * Ticks the agreement to the Terms of Use and Privacy Policy through the checkbox itself (issue
 * #1259), rather than by setting the field - so the control, its two-way binding and the page's own
 * state are what is exercised. The page has to have been rendered first.
 */
function acceptLegalTerms(fixture: ComponentFixture<InviteAcceptanceComponent>): void {
  const checkbox = (fixture.nativeElement as HTMLElement).querySelector(
    '[data-testid="legal-agreement"]'
  ) as HTMLInputElement;
  checkbox.checked = true;
  checkbox.dispatchEvent(new Event('change'));
  fixture.detectChanges();
}

interface Harness {
  fixture: ComponentFixture<InviteAcceptanceComponent>;
  router: Router;
  redeem: jest.Mock;
  accountStateRefresh: jest.Mock;
  accessRefresh: jest.Mock;
}

async function createHarness(options: {
  token?: string | null;
  accountState$?: Observable<AccountStateAnswer>;
  redeem?: jest.Mock;
}): Promise<Harness> {
  const token = options.token === undefined ? 'secret-token' : options.token;
  if (token !== null) {
    storePendingInviteToken(token);
  }
  const accountState$ = options.accountState$ ?? of(known('NoMembership'));
  const redeem = options.redeem ?? jest.fn(() => of({ role: 'Manager' }));
  const accountStateRefresh = jest.fn(() => of(known('Member')));
  const accessRefresh = jest.fn(() => of({ status: 'granted' }));

  await TestBed.configureTestingModule({
    imports: [InviteAcceptanceComponent],
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'welcome', component: WelcomePageComponent },
        { path: 'application-pending', component: PendingPageComponent },
        { path: 'application-rejected', component: RejectedPageComponent },
        { path: 'access-unavailable', component: UnavailablePageComponent },
        { path: 'invite', component: ReopenPageComponent }
      ]),
      { provide: AccountStateService, useValue: { state$: accountState$, refresh: accountStateRefresh } },
      { provide: AccessService, useValue: { refresh: accessRefresh } },
      { provide: InviteRedemptionService, useValue: { redeem } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(InviteAcceptanceComponent);
  return { fixture, router: TestBed.inject(Router), redeem, accountStateRefresh, accessRefresh };
}

/**
 * The invite acceptance page (issue #535, one slice of #505 under #328). It takes the tab's
 * pending invite token into memory (issue #681), lets an eligible account state redeem, and sends every
 * other state to the page its own account state already calls for.
 */
describe('InviteAcceptanceComponent (issue #535)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('takes the pending invite out of the tab storage as soon as it opens, so a reload cannot read it again (issue #681)', async () => {
    const { fixture } = await createHarness({ token: 'super-secret' });

    fixture.detectChanges();

    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toBeNull();
  });

  it('never writes the token to a Storage API or a cookie itself', async () => {
    const { fixture } = await createHarness({ token: 'super-secret' });
    const setItem = jest.spyOn(Storage.prototype, 'setItem');
    const cookie = jest.spyOn(document, 'cookie', 'set');

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();

    expect(setItem).not.toHaveBeenCalled();
    expect(cookie).not.toHaveBeenCalled();
    setItem.mockRestore();
    cookie.mockRestore();
  });

  it('redeems the pending invite token it took', async () => {
    // The signed-out sign-in redirect round trip itself is driven, through the real route table,
    // by invite-address-bar.guard.spec.ts (issue #681); this asserts only the component's own half.
    const redeem = jest.fn(() => of({ role: 'Manager' }));
    const { fixture } = await createHarness({ token: 'route-token', redeem });

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();

    // With the version the page showed beside it (issue #1259), carried back unchanged.
    expect(redeem).toHaveBeenCalledWith('route-token', LEGAL_VERSION);
  });

  it('sends a caller with no pending invite to the open-your-link-again page instead of offering Accept', async () => {
    const redeem = jest.fn(() => of({ role: 'Manager' }));
    const { fixture, router } = await createHarness({ token: null, redeem });

    fixture.detectChanges();
    await fixture.whenStable();

    expect(router.url).toBe('/invite');
    expect(fixture.nativeElement.textContent).not.toContain('Accept');
    expect(redeem).not.toHaveBeenCalled();
  });

  it.each<AccountStateName>(['NoMembership', 'MembershipRevoked', 'ApplicationRejected'])(
    'offers the Accept button to a caller in account state %s',
    async (state) => {
      const { fixture } = await createHarness({ accountState$: of(known(state)) });

      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('Accept');
    }
  );

  it.each<AccountStateName>(['MembershipRevoked', 'ApplicationRejected'])(
    'redeems successfully for a %s caller',
    async (state) => {
      const redeem = jest.fn(() => of({ role: 'Manager' }));
      const { fixture, router, accountStateRefresh, accessRefresh } = await createHarness({
        accountState$: of(known(state)),
        redeem
      });

      fixture.detectChanges();
      acceptLegalTerms(fixture);
      fixture.componentInstance.accept();
      await fixture.whenStable();

      expect(redeem).toHaveBeenCalled();
      expect(accountStateRefresh).toHaveBeenCalled();
      expect(accessRefresh).toHaveBeenCalled();
      expect(router.url).toBe('/');
    }
  );

  it('offers the Accept button, fail-open, when the account state could not be read', async () => {
    const { fixture } = await createHarness({ accountState$: of(UNAVAILABLE) });

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Accept');
  });

  it('re-reads the account state and the access cache, in order, before landing on the dashboard', async () => {
    const calls: string[] = [];
    const { fixture, router, accountStateRefresh, accessRefresh } = await createHarness({
      accountState$: of(known('NoMembership'))
    });
    accountStateRefresh.mockImplementation(() => {
      calls.push('account-state');
      return of(known('Member'));
    });
    accessRefresh.mockImplementation(() => {
      calls.push('access');
      return of({ status: 'granted' });
    });

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();
    await fixture.whenStable();

    expect(calls).toEqual(['account-state', 'access']);
    expect(router.url).toBe('/');
  });

  const nonRedeemableRoutes = (Object.entries(ACCOUNT_STATE_ROUTE) as [AccountStateName, string][]).filter(
    ([state]) => state !== 'NoMembership' && state !== 'MembershipRevoked' && state !== 'ApplicationRejected'
  );

  it.each(nonRedeemableRoutes)(
    'sends a %s caller to their own account-state page instead of the Accept button',
    async (state, path) => {
      const { fixture, router } = await createHarness({ accountState$: of(known(state)) });

      fixture.detectChanges();
      await fixture.whenStable();

      expect(router.url).toBe(`/${path}`);
    }
  );

  it('sends a Member caller to the dashboard instead of the Accept button', async () => {
    const { fixture, router } = await createHarness({ accountState$: of(known('Member')) });

    fixture.detectChanges();
    await fixture.whenStable();

    expect(router.url).toBe('/');
  });

  it.each<[string, number, string]>([
    ['an unusable invite', 400, 'This invite link cannot be used. It may already have been used.'],
    ['a caller who already belongs to a business', 409, 'This person is already a member of a business.'],
    [
      'a caller who must first be released from a deactivated business',
      409,
      'Your account is still attached to a deactivated organisation.'
    ]
  ])('shows the API-provided message for %s', async (_description, status, message) => {
    const redeem = jest.fn(() => throwError(() => new HttpErrorResponse({ status, error: { message } })));
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(message);
  });

  it('falls back to a generic message when the refusal carries none', async () => {
    const redeem = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("couldn't be accepted");
  });

  it('lets a failed redemption be retried rather than leaving the button disabled', async () => {
    const redeem = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { fixture } = await createHarness({ redeem });

    fixture.detectChanges();
    acceptLegalTerms(fixture);
    fixture.componentInstance.accept();
    await fixture.whenStable();

    expect(fixture.componentInstance.redeeming).toBe(false);
  });

  describe('agreeing to the Terms of Use and Privacy Policy (issue #1259)', () => {
    it('shows an unticked required checkbox linking both documents, opening in a new tab', async () => {
      const { fixture } = await createHarness({});

      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;

      const checkbox = host.querySelector('[data-testid="legal-agreement"]') as HTMLInputElement;
      expect(checkbox).not.toBeNull();
      expect(checkbox.checked).toBe(false);
      expect(checkbox.required).toBe(true);

      const label = checkbox.closest('label');
      expect(label?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
        'I agree to the Terms of Use and the Privacy Policy'
      );

      for (const [testId, href] of [
        ['terms-of-use-link', '/terms-of-use'],
        ['privacy-policy-link', '/privacy-policy']
      ]) {
        const link = host.querySelector(`[data-testid="${testId}"]`) as HTMLAnchorElement;
        expect(link.getAttribute('href')).toBe(href);
        expect(link.getAttribute('target')).toBe('_blank');
        expect(link.getAttribute('rel')).toContain('noopener');
      }
    });

    it('disables the Accept button until the agreement is ticked', async () => {
      const { fixture } = await createHarness({});

      fixture.detectChanges();
      const button = (fixture.nativeElement as HTMLElement).querySelector(
        '[data-testid="accept-invite"]'
      ) as HTMLButtonElement;
      expect(button.disabled).toBe(true);

      acceptLegalTerms(fixture);

      expect(button.disabled).toBe(false);
    });

    it('redeems nothing when an accept is reached with the agreement unticked', async () => {
      const redeem = jest.fn(() => of({ role: 'Manager' }));
      const { fixture } = await createHarness({ redeem });

      fixture.detectChanges();
      fixture.componentInstance.accept();

      expect(redeem).not.toHaveBeenCalled();
    });

    it('shows the refresh message exactly as the API writes it when the version is no longer current', async () => {
      const refreshMessage = 'Our Terms of Use have been updated. Please refresh the page and try again.';
      const redeem = jest.fn(() =>
        throwError(() => new HttpErrorResponse({ status: 400, error: { message: refreshMessage } }))
      );
      const { fixture } = await createHarness({ redeem });

      fixture.detectChanges();
      acceptLegalTerms(fixture);
      fixture.componentInstance.accept();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(
        (fixture.nativeElement as HTMLElement).querySelector('[data-testid="invite-error"]')?.textContent
      ).toContain(refreshMessage);

      // And the page stays usable: the person reloads and accepts again rather than being left
      // with a button that can never be pressed.
      expect(fixture.componentInstance.redeeming).toBe(false);
    });

    it('sends no version at all when the account state could not be read, leaving the refusal to the API', async () => {
      const redeem = jest.fn(() => of({ role: 'Manager' }));
      const { fixture } = await createHarness({ accountState$: of(UNAVAILABLE), redeem });

      fixture.detectChanges();
      acceptLegalTerms(fixture);
      fixture.componentInstance.accept();

      expect(redeem).toHaveBeenCalledWith('secret-token', null);
    });
  });

  it('waits for the account state rather than deciding from an unread one', async () => {
    const pending = new Subject<AccountStateAnswer>();
    const { fixture, router } = await createHarness({ accountState$: pending });

    fixture.detectChanges();
    pending.next(known('Member'));
    await fixture.whenStable();

    expect(router.url).toBe('/');
  });
});
