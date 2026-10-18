import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { AccountStateAnswer, AccountStateService } from './account-state.service';
import { WelcomeComponent } from './welcome.component';

@Component({ standalone: true, template: 'invite' })
class InvitePageComponent {}

function known(onboardingEnabled: boolean): AccountStateAnswer {
  return { status: 'known', accountState: { state: 'NoMembership', onboardingEnabled, rejectionReason: null } };
}

async function render(accountState$: Observable<AccountStateAnswer>): Promise<{
  fixture: ComponentFixture<WelcomeComponent>;
  host: HTMLElement;
  router: Router;
}> {
  await TestBed.configureTestingModule({
    imports: [WelcomeComponent],
    providers: [
      provideRouter([{ path: 'invite/:token', component: InvitePageComponent }]),
      { provide: AccountStateService, useValue: { state$: accountState$ } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(WelcomeComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, router: TestBed.inject(Router) };
}

/**
 * The welcome page (issue #542, one slice of #508 under #328): where `accountStateGuard` sends a
 * `NoMembership` caller, offering the invite-link entry point and, when onboarding is enabled, the
 * start-a-new-business choice.
 */
describe('WelcomeComponent (issue #542)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('always offers the invite-link option', async () => {
    const { host } = await render(of(known(false)));

    expect(host.textContent).toContain('invite link');
  });

  it('offers Start a new business when onboarding is enabled', async () => {
    const { host } = await render(of(known(true)));

    expect(host.textContent).toContain('Start a new business');
  });

  it('links Start a new business to the application form (issue #543)', async () => {
    const { host } = await render(of(known(true)));

    expect(host.querySelector('a[href="/apply"]')?.textContent).toContain('Start a new business');
  });

  it('does not offer Start a new business when onboarding is disabled', async () => {
    const { host } = await render(of(known(false)));

    expect(host.textContent).not.toContain('Start a new business');
  });

  it('navigates to the invite acceptance route for a pasted full invite link', async () => {
    const { fixture, router } = await render(of(known(false)));

    fixture.componentInstance.inviteInput = 'https://app.example.com/invite/secret-token';
    fixture.componentInstance.goToInvite();
    await fixture.whenStable();

    expect(router.url).toBe('/invite/secret-token');
  });

  it('navigates to the invite acceptance route for a pasted bare token', async () => {
    const { fixture, router } = await render(of(known(false)));

    fixture.componentInstance.inviteInput = 'secret-token';
    fixture.componentInstance.goToInvite();
    await fixture.whenStable();

    expect(router.url).toBe('/invite/secret-token');
  });

  it('shows an error and navigates nowhere for a blank invite field', async () => {
    const { fixture, router } = await render(of(known(false)));

    fixture.componentInstance.inviteInput = '   ';
    fixture.componentInstance.goToInvite();
    await fixture.whenStable();

    expect(fixture.componentInstance.inviteError).not.toBeNull();
    expect(router.url).not.toContain('/invite/');
  });

  /**
   * Issue #711, design § 12. Identity linking is deferred, so a member who normally signs in one
   * way and then signs in another arrives *here*, with no membership and no inherited access. This
   * page is the only place that can tell them why, and the design requires it to.
   */
  it('explains that signing in a different way is a separate account with no inherited access', async () => {
    const { fixture } = await render(of(known(false)));
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('[data-testid="welcome-separate-accounts"]') as HTMLElement;

    expect(note).not.toBeNull();
    expect(note.textContent).toContain('separate account');
    expect(note.textContent).toContain('never joins you to a business you already belong to');
  });

  it('shows that explanation whether or not self-service onboarding is offered', async () => {
    for (const onboardingEnabled of [true, false]) {
      TestBed.resetTestingModule();
      const { fixture } = await render(of(known(onboardingEnabled)));

      expect(fixture.nativeElement.querySelector('[data-testid="welcome-separate-accounts"]')).not.toBeNull();
    }
  });
});
