import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AccessService, MemberAccessState } from './access.service';
import { Entitlement } from './entitlement';
import { PLAN_LOCKED_PATH } from './plan-lock';
import { PlanLockedComponent } from './plan-locked.component';

@Component({ standalone: true, template: 'elsewhere' })
class BlankPageComponent {}

interface Rendered {
  fixture: ComponentFixture<PlanLockedComponent>;
  host: HTMLElement;
  access$: BehaviorSubject<MemberAccessState>;
}

function entitlement(plan: string | null, enforced = true): Entitlement {
  return { enforced, plan, source: 'BillingSubscription', endsAtUtc: null };
}

function granted(entitlementValue: Entitlement | undefined): MemberAccessState {
  return {
    status: 'granted',
    access: {
      role: 'Owner',
      capabilities: new Set(),
      businessName: 'Vending Co',
      timeZoneId: 'Australia/Sydney',
      entitlement: entitlementValue
    }
  };
}

/**
 * Renders the page the way `requirePlan` reaches it: at `/needs-plan?from=<refused URL>`, over a
 * stubbed `AccessService.access$`.
 */
async function render(options: { from?: string; entitlementValue?: Entitlement } = {}): Promise<Rendered> {
  const access$ = new BehaviorSubject<MemberAccessState>(granted(options.entitlementValue ?? entitlement('Free')));

  await TestBed.configureTestingModule({
    imports: [PlanLockedComponent],
    providers: [
      provideRouter([
        { path: PLAN_LOCKED_PATH, component: BlankPageComponent },
        { path: '**', component: BlankPageComponent }
      ]),
      { provide: AccessService, useValue: { access$ } }
    ]
  }).compileComponents();

  const url =
    options.from === undefined
      ? `/${PLAN_LOCKED_PATH}`
      : `/${PLAN_LOCKED_PATH}?from=${encodeURIComponent(options.from)}`;
  await TestBed.inject(Router).navigateByUrl(url);

  const fixture = TestBed.createComponent(PlanLockedComponent);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, access$ };
}

/**
 * The "needs a plan" page (issue #979). It explains and never refuses: the endpoints behind the
 * page it stands in for enforce the plan themselves, so what is asserted here is whether a member
 * is told the right thing and offered the one place that could change it.
 */
describe('PlanLockedComponent (issue #979)', () => {
  it('names the destination and the plan it needs', async () => {
    const { host } = await render({ from: '/sites', entitlementValue: entitlement('Free') });

    expect(host.querySelector('h1')?.textContent).toContain('Sites needs the Pro plan');
    expect(host.textContent).toContain('Sites needs the Pro plan.');
  });

  it('links to the Plan page rather than offering a Connect action', async () => {
    const { host } = await render({ from: '/sites', entitlementValue: entitlement('Free') });

    expect(host.querySelector<HTMLAnchorElement>('a[href="/plan"]')).not.toBeNull();
    expect(host.querySelector('a[href="/admin/nayax-settings"]')).toBeNull();
  });

  it('says the destination is available once the entitlement allows it', async () => {
    const { fixture, host, access$ } = await render({ from: '/sites', entitlementValue: entitlement('Free') });
    expect(host.textContent).toContain('Sites needs the Pro plan.');

    access$.next(granted(entitlement('Pro')));
    fixture.detectChanges();

    expect(host.textContent).toContain("This business's plan now includes Sites.");
    expect(host.querySelector<HTMLAnchorElement>('a[href="/sites"]')?.textContent).toContain('Open Sites');
  });

  it('gives a generic explanation for a URL that names no plan-locked destination', async () => {
    const { host } = await render();

    expect(host.querySelector('h1')?.textContent).toContain('This page needs a different plan');
  });

  it('re-reads the entitlement rather than trusting the guard, reporting the current plan', async () => {
    const { host } = await render({ from: '/reports/bookkeeping', entitlementValue: entitlement('Pro') });

    expect(host.textContent).toContain("This business's plan now includes Bookkeeping.");
  });
});
