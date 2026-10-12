import { MsalGuard } from '@azure/msal-angular';
import { Route } from '@angular/router';
import { ACCESS_NOT_ALLOWED_PATH } from './access/capability.guard';
import { accountStateGuard } from './access/account-state.guard';
import { platformAdminGuard } from './access/platform-admin.guard';
import { AccessNotAllowedComponent } from './access/access-not-allowed.component';
import { AccessUnavailableComponent } from './access/access-unavailable.component';
import { OnboardingPlaceholderComponent } from './access/onboarding-placeholder.component';
import { routes } from './app.routes';
import { StockHistoryPageComponent } from './components/stock/stock-history-page.component';
import { AdminImportsComponent } from './components/admin/imports/admin-imports.component';
import { HistoricalCostRecoveryComponent } from './components/admin/historical-cost-recovery/historical-cost-recovery.component';
import { AvcoTransitionComponent } from './components/admin/avco-transition/avco-transition.component';
import { CostingRepairPageComponent } from './components/admin/costing-repair/costing-repair-page.component';
import { HistoricalGstClassificationComponent } from './components/admin/historical-gst-classification/historical-gst-classification.component';
import { NayaxSaleTimestampRepairComponent } from './components/admin/nayax-sale-timestamp-repair/nayax-sale-timestamp-repair.component';
import { PlatformDiagnosticsComponent } from './components/admin/diagnostics/platform-diagnostics.component';
import { PurchaseEditPageComponent } from './components/purchases/purchase-edit/purchase-edit-page.component';
import { PurchaseListComponent } from './components/purchases/purchase-list.component';
import { PurchaseUploadComponent } from './components/purchases/purchase-upload.component';
import { SupplierOrdersComponent } from './components/purchases/supplier-orders.component';

function route(path: string): Route {
  const match = routes.find((candidate) => candidate.path === path);
  expect(match).toBeDefined();
  return match!;
}

async function loadedComponent(path: string): Promise<unknown> {
  const loader = route(path).loadComponent;
  expect(loader).toBeDefined();
  return loader!();
}

describe('Stock History routes', () => {
  it('serves the global Stock History page at /stock-history, behind authentication', async () => {
    expect(route('stock-history').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('stock-history')).resolves.toBe(StockHistoryPageComponent);
  });

  /**
   * The product "Stock" links in the product lists point at /products/:id/stock. That entry point
   * keeps working and reaches the global page, which preselects the product from the route
   * parameter (see stock-history-page.component.spec.ts).
   */
  it('keeps the legacy product entry point and resolves it to the same page', async () => {
    expect(route('products/:id/stock').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('products/:id/stock')).resolves.toBe(StockHistoryPageComponent);
  });
});

describe('Purchase edit route (issue #475)', () => {
  it('serves the dedicated Edit purchase page at /purchases/:id/edit, behind authentication', async () => {
    expect(route('purchases/:id/edit').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('purchases/:id/edit')).resolves.toBe(PurchaseEditPageComponent);
  });

  /**
   * `purchases/new` and `purchases/orders` are two-segment paths, so the three-segment edit pattern
   * cannot shadow them in either declaration order. This pins that none of the purchase routes
   * started resolving to a different page when the edit route arrived.
   */
  it('leaves the existing purchase routes resolving to their own pages', async () => {
    await expect(loadedComponent('purchases')).resolves.toBe(PurchaseListComponent);
    await expect(loadedComponent('purchases/new')).resolves.toBe(PurchaseUploadComponent);
    await expect(loadedComponent('purchases/orders')).resolves.toBe(SupplierOrdersComponent);
  });
});

describe('Admin Imports route (issue #389)', () => {
  it('serves the dedicated Admin Imports page at /admin/imports, behind authentication', async () => {
    expect(route('admin/imports').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/imports')).resolves.toBe(AdminImportsComponent);
  });
});

describe('Admin costing and maintenance routes (issue #390)', () => {
  it('serves the dedicated Nayax Historical Cost Recovery page, behind authentication', async () => {
    expect(route('admin/historical-cost-recovery').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/historical-cost-recovery')).resolves.toBe(HistoricalCostRecoveryComponent);
  });

  it('serves the dedicated Inventory AVCO Transition Baseline page, behind authentication', async () => {
    expect(route('admin/avco-transition').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/avco-transition')).resolves.toBe(AvcoTransitionComponent);
  });

  it('serves the dedicated Costing Repair page, behind authentication', async () => {
    expect(route('admin/costing-repair').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/costing-repair')).resolves.toBe(CostingRepairPageComponent);
  });

  it('serves the dedicated Historical GST Classification page, behind authentication (issue #433)', async () => {
    expect(route('admin/historical-gst-classification').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/historical-gst-classification')).resolves.toBe(
      HistoricalGstClassificationComponent
    );
  });

  /**
   * The Nayax Sale Timestamp Repair page (#487, the UI over #472's Preview/Apply API) is routed
   * like every other Admin maintenance page, so the URL is reachable directly by an authenticated
   * operator; the API authorizes and business-scopes both of its requests itself.
   */
  it('serves the dedicated Nayax Sale Timestamp Repair page, behind authentication (issue #487)', async () => {
    expect(route('admin/nayax-sale-timestamp-repair').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/nayax-sale-timestamp-repair')).resolves.toBe(
      NayaxSaleTimestampRepairComponent
    );
  });

  /**
   * The super-admin diagnostics page (#335), moved under `/super-admin` in issue #546.
   * `platformAdminGuard` asks the same platform-admin check the page itself asks on arrival, so
   * entering the URL directly still reaches a page that shows a refusal, not data, whichever of
   * the two answers first.
   */
  it('serves the Platform Diagnostics page behind the platform-admin guard, not a frontend role (issues #335, #546)', async () => {
    expect(route('super-admin/diagnostics').canActivate).toEqual([MsalGuard, platformAdminGuard]);
    await expect(loadedComponent('super-admin/diagnostics')).resolves.toBe(PlatformDiagnosticsComponent);
  });

  /** The old address (#335) redirects to its new home (#546) rather than serving the page. */
  it('redirects the old /admin/diagnostics address to /super-admin/diagnostics', () => {
    const redirect = route('admin/diagnostics');

    expect(redirect.redirectTo).toBe('/super-admin/diagnostics');
    expect(redirect.pathMatch).toBe('full');
    expect(redirect.canActivate).toBeUndefined();
  });

  /**
   * `/admin` keeps its own address rather than redirecting: the Dashboard and each dedicated
   * page's "Back to Admin" link still point at it, alongside the sidebar's Admin group (#391).
   */
  it('keeps /admin as the Admin landing page rather than redirecting it', async () => {
    const admin = route('admin');
    expect(admin.redirectTo).toBeUndefined();
    expect(admin.canActivate).toContain(MsalGuard);
  });
});

/**
 * Capability route guards (issue #528). They are presentation only - every endpoint behind every
 * page checks the same capability itself - but a page nobody's role allows must not be left
 * rendering a screen full of refusals, so each authenticated route declares one alongside
 * `MsalGuard`.
 */
describe('capability route guards (issue #528)', () => {
  function authenticatedRoutes(): Route[] {
    return routes.filter((candidate) => (candidate.canActivate ?? []).includes(MsalGuard));
  }

  /**
   * `/access-not-allowed` is where a capability guard *sends* a member, so guarding it by one
   * would send them away from their own explanation. The account-state routing destinations and
   * the invite route (issue #529) are the other exceptions, for the same reason `accountStateGuard`
   * is absent from them - see the `account-state routing and the global 403 handler` describe
   * block below. `/super-admin/diagnostics` is decided by the diagnostics API per request rather
   * than by any business role (issue #335) and carries `platformAdminGuard` instead (issue #546,
   * see the describe block below), so it is guarded but not by a capability and does not appear
   * here.
   */
  it('guards every authenticated route with a capability, except the ones that cannot carry one', () => {
    const unguarded = authenticatedRoutes()
      .filter((candidate) => (candidate.canActivate ?? []).length < 2)
      .map((candidate) => candidate.path ?? '');

    expect(unguarded).toEqual([
      ACCESS_NOT_ALLOWED_PATH,
      'welcome',
      'application-pending',
      'application-rejected',
      'access-unavailable',
      'invite/:token'
    ]);
  });

  it('keeps MsalGuard first on every route it guards, so authentication is still decided first', () => {
    for (const candidate of authenticatedRoutes()) {
      expect((candidate.canActivate ?? [])[0]).toBe(MsalGuard);
    }
  });

  it('serves the access-not-allowed page behind authentication alone', async () => {
    expect(route(ACCESS_NOT_ALLOWED_PATH).canActivate).toContain(MsalGuard);
    await expect(loadedComponent(ACCESS_NOT_ALLOWED_PATH)).resolves.toBe(AccessNotAllowedComponent);
  });

  it('keeps the public Entra callback route free of any guard', () => {
    expect(route('auth').canActivate).toBeUndefined();
  });
});

/**
 * Account-state routing (issue #529): `accountStateGuard` runs before the capability guard on
 * every business route, so the account-state read settles before that page's own business calls
 * ever run, and it is absent from the routes it would otherwise send somebody straight back to.
 */
describe('account-state routing (issue #529)', () => {
  function capabilityGuardedRoutes(): Route[] {
    return routes.filter((candidate) => (candidate.canActivate ?? []).length >= 3);
  }

  const bypassPaths = ['welcome', 'application-pending', 'application-rejected', 'access-unavailable', 'invite/:token'];

  it('puts accountStateGuard second, after MsalGuard and before the capability guard, on every business route', () => {
    for (const candidate of capabilityGuardedRoutes()) {
      const guards = candidate.canActivate ?? [];
      expect(guards[0]).toBe(MsalGuard);
      expect(guards[1]).toBe(accountStateGuard);
    }
  });

  it('carries no accountStateGuard on the account-state destinations or the invite route', () => {
    for (const path of bypassPaths) {
      expect(route(path).canActivate ?? []).not.toContain(accountStateGuard);
    }
  });

  it('serves the welcome, application-pending and application-rejected placeholders, behind authentication alone', () => {
    expect(route('welcome').component).toBe(OnboardingPlaceholderComponent);
    expect(route('welcome').data).toEqual({ heading: 'Welcome' });
    expect(route('application-pending').component).toBe(OnboardingPlaceholderComponent);
    expect(route('application-rejected').component).toBe(OnboardingPlaceholderComponent);
  });

  it('serves the invite placeholder behind authentication alone', () => {
    expect(route('invite/:token').component).toBe(OnboardingPlaceholderComponent);
  });

  it('serves the real access-unavailable page, behind authentication alone', () => {
    expect(route('access-unavailable').canActivate).toEqual([MsalGuard]);
    expect(route('access-unavailable').component).toBe(AccessUnavailableComponent);
  });
});

/**
 * `/super-admin/*` routing (issue #546). `platformAdminGuard` is neither a capability guard nor
 * `accountStateGuard`: platform-admin authority has nothing to do with business membership or
 * role, so a refusal belongs on the dashboard, never on a membership-handling destination.
 */
describe('super-admin routing (issue #546)', () => {
  it('guards /super-admin/diagnostics with MsalGuard then platformAdminGuard, and nothing else', () => {
    expect(route('super-admin/diagnostics').canActivate).toEqual([MsalGuard, platformAdminGuard]);
  });

  it('carries no accountStateGuard, because platform-admin authority is independent of membership', () => {
    expect(route('super-admin/diagnostics').canActivate ?? []).not.toContain(accountStateGuard);
  });
});
