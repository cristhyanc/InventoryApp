import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AccessUnavailableComponent } from './access-unavailable.component';
import { AccountStateAnswer, AccountStateService } from './account-state.service';

async function render(answer: AccountStateAnswer): Promise<HTMLElement> {
  await TestBed.configureTestingModule({
    imports: [AccessUnavailableComponent],
    providers: [provideRouter([]), { provide: AccountStateService, useValue: { state$: of(answer) } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(AccessUnavailableComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

function known(
  state: 'BusinessDeactivated' | 'MembershipAmbiguous' | 'MembershipRevoked',
  onboardingEnabled = false
): AccountStateAnswer {
  return { status: 'known', accountState: { state, onboardingEnabled, rejectionReason: null } };
}

/**
 * The access-unavailable page (issue #529, agreed under #328's contract corrections). It names no
 * business, and the options it offers follow the API eligibility rule rather than a frontend
 * guess: only `MembershipRevoked` is eligible to redeem an invite or apply for a new business.
 */
describe('AccessUnavailableComponent (issue #529)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('tells a deactivated-business member to contact the Owner, and names no business', async () => {
    const host = await render(known('BusinessDeactivated'));

    expect(host.textContent).toContain('Owner');
    expect(host.textContent).not.toContain('Vending Co');
  });

  it('tells an ambiguous-membership person to contact support', async () => {
    const host = await render(known('MembershipAmbiguous'));

    expect(host.textContent).toContain('support');
  });

  it('offers neither option to a deactivated-business member', async () => {
    const host = await render(known('BusinessDeactivated', true));

    expect(host.textContent).not.toContain('invite link');
    expect(host.textContent).not.toContain('Start a new business');
  });

  it('offers neither option to an ambiguous-membership person', async () => {
    const host = await render(known('MembershipAmbiguous', true));

    expect(host.textContent).not.toContain('invite link');
    expect(host.textContent).not.toContain('Start a new business');
  });

  /** AC: "both options" with onboarding on. */
  it('offers both the invite option and start-a-new-business to a revoked member when onboarding is enabled', async () => {
    const host = await render(known('MembershipRevoked', true));

    expect(host.textContent).toContain('invite link');
    expect(host.textContent).toContain('Start a new business');
    expect(host.querySelector('a[href="/apply"]')?.textContent).toContain('Start a new business');
  });

  /** AC: "only the invite option" with onboarding off. */
  it('offers only the invite option to a revoked member when onboarding is disabled', async () => {
    const host = await render(known('MembershipRevoked', false));

    expect(host.textContent).toContain('invite link');
    expect(host.textContent).not.toContain('Start a new business');
  });
});
