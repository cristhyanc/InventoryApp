import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { ApplicationRejectedComponent } from './application-rejected.component';
import { AccountStateAnswer, AccountStateService } from './account-state.service';

@Component({ standalone: true, template: 'invite' })
class InvitePageComponent {}

function known(rejectionReason: string | null, onboardingEnabled = false): AccountStateAnswer {
  return { status: 'known', accountState: { state: 'ApplicationRejected', onboardingEnabled, rejectionReason } };
}

async function render(accountState$: Observable<AccountStateAnswer>): Promise<{
  fixture: ComponentFixture<ApplicationRejectedComponent>;
  host: HTMLElement;
  router: Router;
}> {
  await TestBed.configureTestingModule({
    imports: [ApplicationRejectedComponent],
    providers: [
      provideRouter([{ path: 'invite/:token', component: InvitePageComponent }]),
      { provide: AccountStateService, useValue: { state$: accountState$ } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ApplicationRejectedComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, router: TestBed.inject(Router) };
}

/**
 * The rejected-application page (issue #542, one slice of #508 under #328): where
 * `accountStateGuard` sends an `ApplicationRejected` caller.
 */
describe('ApplicationRejectedComponent (issue #542)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it("shows the Super Admin's recorded reason when there is one", async () => {
    const { host } = await render(of(known('Missing required documentation.')));

    expect(host.textContent).toContain('Missing required documentation.');
  });

  it('falls back to a generic sentence when no reason was recorded', async () => {
    const { host } = await render(of(known(null)));

    expect(host.textContent).toContain('not approved');
  });

  it('always offers the invite-link option', async () => {
    const { host } = await render(of(known(null)));

    expect(host.textContent).toContain('invite link');
  });

  it('offers Apply again when onboarding is enabled', async () => {
    const { host } = await render(of(known(null, true)));

    expect(host.textContent).toContain('Apply again');
  });

  it('links Apply again to the application form (issue #543)', async () => {
    const { host } = await render(of(known(null, true)));

    expect(host.querySelector('a[href="/apply"]')?.textContent).toContain('Apply again');
  });

  it('does not offer Apply again when onboarding is disabled', async () => {
    const { host } = await render(of(known(null, false)));

    expect(host.textContent).not.toContain('Apply again');
  });

  it('navigates to the invite acceptance route for a pasted invite link', async () => {
    const { fixture, router } = await render(of(known(null)));

    fixture.componentInstance.inviteInput = 'https://app.example.com/invite/secret-token';
    fixture.componentInstance.goToInvite();
    await fixture.whenStable();

    expect(router.url).toBe('/invite/secret-token');
  });

  it('shows an error and navigates nowhere for a blank invite field', async () => {
    const { fixture, router } = await render(of(known(null)));

    fixture.componentInstance.inviteInput = '   ';
    fixture.componentInstance.goToInvite();
    await fixture.whenStable();

    expect(fixture.componentInstance.inviteError).not.toBeNull();
    expect(router.url).not.toContain('/invite/');
  });
});
