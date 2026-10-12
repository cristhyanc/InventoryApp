import { MsalGuard } from '@azure/msal-angular';
import { Route } from '@angular/router';
import { ACCESS_NOT_ALLOWED_PATH } from './access/capability.guard';
import { AccessNotAllowedComponent } from './access/access-not-allowed.component';
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
   * The super-admin diagnostics page (#335) is routed like any other authenticated page: the
   * guard only requires a signed-in actor, because platform-admin access is decided by the
   * diagnostics API on every request rather than by the router. Entering the URL directly
   * therefore reaches a page that shows a refusal, not data.
   */
  it('serves the Platform Diagnostics page behind ordinary authentication, not a frontend role (issue #335)', async () => {
    expect(route('admin/diagnostics').canActivate).toContain(MsalGuard);
    await expect(loadedComponent('admin/diagnostics')).resolves.toBe(PlatformDiagnosticsComponent);
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
   * The two exceptions are deliberate and are the whole list: `/access-not-allowed` is where a
   * capability guard *sends* a member, so guarding it by one would send them away from their own
   * explanation; `/admin/diagnostics` is decided by the diagnostics API per request rather than by
   * any business role, and shows a refusal instead of data (issue #335).
   */
  it('guards every authenticated route with a capability, except the two that cannot carry one', () => {
    const unguarded = authenticatedRoutes()
      .filter((candidate) => (candidate.canActivate ?? []).length < 2)
      .map((candidate) => candidate.path ?? '');

    expect(unguarded).toEqual([ACCESS_NOT_ALLOWED_PATH, 'admin/diagnostics']);
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
