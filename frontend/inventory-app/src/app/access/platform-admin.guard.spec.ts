import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { platformAdminGuard } from './platform-admin.guard';

@Component({ standalone: true, template: 'diagnostics' })
class DiagnosticsPageComponent {}

@Component({ standalone: true, template: 'dashboard' })
class DashboardPageComponent {}

async function withGuard(isGranted: () => Observable<boolean>): Promise<Router> {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: '', component: DashboardPageComponent },
        { path: 'super-admin/diagnostics', component: DiagnosticsPageComponent, canActivate: [platformAdminGuard] }
      ]),
      { provide: PlatformDiagnosticsAccessService, useValue: { isGranted } }
    ]
  }).compileComponents();

  return TestBed.inject(Router);
}

/**
 * `platformAdminGuard` (issue #546): presentation in front of `/super-admin/*`, reusing the same
 * cached probe the sidebar's `Super Admin` group asks. A refusal goes to the dashboard, never to
 * any membership-handling destination - platform-admin authority has nothing to do with business
 * membership or role capability.
 */
describe('platformAdminGuard (issue #546)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lets the configured platform administrator through', async () => {
    const router = await withGuard(() => of(true));

    await router.navigateByUrl('/super-admin/diagnostics');

    expect(router.url).toBe('/super-admin/diagnostics');
  });

  it('sends a refusal to the dashboard, not to any membership-handling destination', async () => {
    const router = await withGuard(() => of(false));

    await router.navigateByUrl('/super-admin/diagnostics');

    expect(router.url).toBe('/');
  });
});
