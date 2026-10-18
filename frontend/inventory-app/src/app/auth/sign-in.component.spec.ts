import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PENDING_INVITE_TOKEN_KEY, storePendingInviteToken } from '../access/pending-invite-token';
import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import { SignInComponent } from './sign-in.component';
import { SignInFailureKind, SignInService } from './sign-in.service';

interface Harness {
  fixture: ComponentFixture<SignInComponent>;
  signIn: jest.Mock;
}

function createHarness(failure: SignInFailureKind | null = null): Harness {
  const signIn = jest.fn().mockResolvedValue(undefined);
  let reported: SignInFailureKind | null = failure;

  TestBed.configureTestingModule({
    imports: [SignInComponent],
    providers: [
      {
        provide: SignInService,
        useValue: {
          signIn,
          take: () => {
            const taken = reported;
            reported = null;
            return taken;
          },
          report: jest.fn()
        }
      }
    ]
  });

  return { fixture: TestBed.createComponent(SignInComponent), signIn };
}

function button(fixture: ComponentFixture<SignInComponent>, testId: string): HTMLButtonElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
}

/**
 * The sign-in page (issue #711, extended by #712): where the person chooses an authority before
 * any redirect starts, and where the one customer identity action now names both hosted journeys -
 * Google and a local email address with a password.
 */
describe('SignInComponent (issue #711)', () => {
  beforeEach(() => {
    sessionStorage.clear();
    unconfigureExternalId();
  });

  afterEach(() => {
    unconfigureExternalId();
    TestBed.resetTestingModule();
  });

  describe('with no customer identity tenant configured (the shipped state)', () => {
    it('offers the Microsoft work account action alone', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      expect(button(fixture, 'sign-in-customer-identity')).toBeNull();
      expect(button(fixture, 'sign-in-workforce')).not.toBeNull();
    });

    /**
     * Nothing may promise an email address journey a deployment has no provider for (issue #712).
     */
    it('says nothing about passwords, registration or recovery', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="sign-in-password-help"]')).toBeNull();
      expect(fixture.nativeElement.textContent).not.toContain('password');
    });
  });

  describe('with a customer identity tenant configured', () => {
    beforeEach(() => configureExternalId());

    /**
     * The action names **both** hosted journeys since issue #712: one hosted user flow offers
     * Google and a local email address with a password, and no provider-selection parameter is
     * confirmed, so one action is the honest shape - a second button would send the person to the
     * identical page.
     */
    it('offers one customer identity action naming Google and an email address', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      const customerIdentity = button(fixture, 'sign-in-customer-identity')!;
      expect(customerIdentity.textContent?.trim()).toBe('Continue with Google or an email address');
      expect(customerIdentity.getAttribute('type')).toBe('button');

      // The action is described by the line that explains the hosted flow, so a screen reader
      // announces "you will finish signing in on Microsoft's page" with the action itself.
      const describedBy = customerIdentity.getAttribute('aria-describedby')!;
      const hint = fixture.nativeElement.querySelector(`#${describedBy}`) as HTMLElement;
      expect(hint.textContent).toContain('Microsoft');
      expect(hint.textContent).toContain('Google');
      expect(hint.textContent).toContain('email address');
    });

    /**
     * Registering, verifying the mailbox and resetting a password all happen on the provider's own
     * hosted pages (issue #712, design § 16's #712 row): this application has no password field,
     * no reset form and no recovery link of its own, and the page has to say where those things
     * are instead of offering a journey it does not own. A verification or reset code that has
     * expired is the same answer - start again from here and the hosted page issues a new one.
     */
    it('points registration, mailbox verification and password reset at the hosted pages', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      const help = fixture.nativeElement.querySelector('[data-testid="sign-in-password-help"]') as HTMLElement;

      expect(help).not.toBeNull();
      expect(help.textContent).toContain('create an account');
      expect(help.textContent).toContain('verify');
      expect(help.textContent).toContain('reset your password');
      expect(help.textContent).toContain('never');
      expect(help.textContent).toContain('expired');
    });

    it('offers no password, code or recovery field of its own', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('input').length).toBe(0);
      expect(fixture.nativeElement.querySelectorAll('form').length).toBe(0);
    });

    it('signs the customer identity action in against the customer identity authority', () => {
      const { fixture, signIn } = createHarness();
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();

      expect(signIn).toHaveBeenCalledWith('externalId');
    });

    it('keeps the Microsoft work account action signing in against the workforce authority', () => {
      const { fixture, signIn } = createHarness();
      fixture.detectChanges();

      button(fixture, 'sign-in-workforce')!.click();

      expect(signIn).toHaveBeenCalledWith('workforce');
    });

    it('disables both actions and reports progress while a redirect is starting', () => {
      const { fixture, signIn } = createHarness();
      signIn.mockReturnValue(new Promise(() => undefined));
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();
      fixture.detectChanges();

      const customerIdentity = button(fixture, 'sign-in-customer-identity')!;
      expect(customerIdentity.disabled).toBe(true);
      expect(customerIdentity.getAttribute('aria-busy')).toBe('true');
      expect(button(fixture, 'sign-in-workforce')!.disabled).toBe(true);
      expect(customerIdentity.textContent).toContain('Taking you to sign in');
    });

    it('starts one redirect however often the action is pressed', () => {
      const { fixture, signIn } = createHarness();
      signIn.mockReturnValue(new Promise(() => undefined));
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();
      button(fixture, 'sign-in-customer-identity')!.click();
      button(fixture, 'sign-in-workforce')!.click();

      expect(signIn).toHaveBeenCalledTimes(1);
    });

    it('says the two ways of signing in are separate accounts', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      const note = fixture.nativeElement.querySelector('[data-testid="sign-in-separate-accounts"]') as HTMLElement;
      expect(note.textContent).toContain('separate account');
    });

    /**
     * A cancelled hosted journey comes back to `/auth`, which reports it here. The page must say
     * what happened and wait: starting another redirect by itself is how a callback loops.
     */
    it('explains a cancelled sign-in and offers the choices again without redirecting', () => {
      const { fixture, signIn } = createHarness('cancelled');
      fixture.detectChanges();

      const error = fixture.nativeElement.querySelector('[data-testid="sign-in-error"]') as HTMLElement;
      expect(error.getAttribute('role')).toBe('alert');
      expect(error.textContent).toContain('cancelled');
      expect(signIn).not.toHaveBeenCalled();
      expect(button(fixture, 'sign-in-customer-identity')!.disabled).toBe(false);
    });

    it('explains a failed sign-in differently from a cancelled one', () => {
      const { fixture } = createHarness('failed');
      fixture.detectChanges();

      const error = fixture.nativeElement.querySelector('[data-testid="sign-in-error"]') as HTMLElement;
      expect(error.textContent).toContain('could not finish');
      expect(error.textContent).not.toContain('cancelled');
    });

    it('shows nothing alarming when the page is simply visited', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="sign-in-error"]')).toBeNull();
    });

    it('gives the actions back and explains a redirect that could not start at all', async () => {
      const { fixture, signIn } = createHarness();
      signIn.mockRejectedValue({ errorCode: 'network_error' });
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(fixture.componentInstance.startingAuthority).toBeNull();
      expect(button(fixture, 'sign-in-customer-identity')!.disabled).toBe(false);
      expect(fixture.nativeElement.querySelector('[data-testid="sign-in-error"]').textContent).toContain(
        'could not finish'
      );
    });

    /**
     * A failed credential journey must not tell the person opposite whether the mailbox they typed
     * has an account here (issue #712). The provider's own error text is where that leaks from -
     * it names the account, the policy and sometimes the reason - so the page renders one of its
     * two fixed sentences and never anything the failure carried.
     */
    it('shows one generic sentence for a refused sign-in, never the provider description', async () => {
      const { fixture, signIn } = createHarness();
      signIn.mockRejectedValue({
        errorCode: 'invalid_grant',
        errorMessage: 'AADSTS50034: The user account customer@example.test does not exist in the directory.',
        errorDesc: 'The password is incorrect for customer@example.test.'
      });
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      const rendered = fixture.nativeElement.textContent as string;
      expect(fixture.nativeElement.querySelector('[data-testid="sign-in-error"]').textContent).toContain(
        'could not finish'
      );
      expect(rendered).not.toContain('does not exist');
      expect(rendered).not.toContain('password is incorrect');
      expect(rendered).not.toContain('AADSTS50034');
      expect(rendered).not.toContain('customer@example.test');
    });

    /**
     * An invite token waits in the tab's own store while its holder signs in, and nothing about
     * choosing an authority may touch it: a hosted sign-in or sign-up on its own never redeems an
     * invite (design § 10.2, and this issue's own criterion).
     */
    it('leaves a waiting invite exactly where it is', () => {
      storePendingInviteToken('an-invite-token');
      const { fixture } = createHarness();
      fixture.detectChanges();

      button(fixture, 'sign-in-customer-identity')!.click();

      expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toContain('an-invite-token');
      expect(fixture.nativeElement.textContent).not.toContain('an-invite-token');
    });
  });
});
