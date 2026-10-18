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
 * The sign-in page (issue #711): where the person chooses an authority before any redirect starts.
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

      expect(button(fixture, 'sign-in-google')).toBeNull();
      expect(button(fixture, 'sign-in-workforce')).not.toBeNull();
    });
  });

  describe('with a customer identity tenant configured', () => {
    beforeEach(() => configureExternalId());

    it('offers a visible Continue with Google action that says where it takes you', () => {
      const { fixture } = createHarness();
      fixture.detectChanges();

      const google = button(fixture, 'sign-in-google')!;
      expect(google.textContent?.trim()).toBe('Continue with Google');
      expect(google.getAttribute('type')).toBe('button');

      // The action is described by the line that explains the hosted flow, so a screen reader
      // announces "you will finish signing in on Microsoft's page" with the action itself.
      const describedBy = google.getAttribute('aria-describedby')!;
      const hint = fixture.nativeElement.querySelector(`#${describedBy}`) as HTMLElement;
      expect(hint.textContent).toContain('Microsoft');
      expect(hint.textContent).toContain('Google');
    });

    it('signs Continue with Google in against the customer identity authority', () => {
      const { fixture, signIn } = createHarness();
      fixture.detectChanges();

      button(fixture, 'sign-in-google')!.click();

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

      button(fixture, 'sign-in-google')!.click();
      fixture.detectChanges();

      const google = button(fixture, 'sign-in-google')!;
      expect(google.disabled).toBe(true);
      expect(google.getAttribute('aria-busy')).toBe('true');
      expect(button(fixture, 'sign-in-workforce')!.disabled).toBe(true);
      expect(google.textContent).toContain('Taking you to sign in');
    });

    it('starts one redirect however often the action is pressed', () => {
      const { fixture, signIn } = createHarness();
      signIn.mockReturnValue(new Promise(() => undefined));
      fixture.detectChanges();

      button(fixture, 'sign-in-google')!.click();
      button(fixture, 'sign-in-google')!.click();
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
      expect(button(fixture, 'sign-in-google')!.disabled).toBe(false);
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

      button(fixture, 'sign-in-google')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(fixture.componentInstance.startingAuthority).toBeNull();
      expect(button(fixture, 'sign-in-google')!.disabled).toBe(false);
      expect(fixture.nativeElement.querySelector('[data-testid="sign-in-error"]').textContent).toContain(
        'could not finish'
      );
    });

    /**
     * An invite token waits in the tab's own store while its holder signs in, and nothing about
     * choosing an authority may touch it: Google sign-in on its own never redeems an invite
     * (design § 10.2, and this issue's own criterion).
     */
    it('leaves a waiting invite exactly where it is', () => {
      storePendingInviteToken('an-invite-token');
      const { fixture } = createHarness();
      fixture.detectChanges();

      button(fixture, 'sign-in-google')!.click();

      expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toContain('an-invite-token');
      expect(fixture.nativeElement.textContent).not.toContain('an-invite-token');
    });
  });
});
