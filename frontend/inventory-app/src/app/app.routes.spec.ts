import { MsalGuard } from '@azure/msal-angular';
import { Route } from '@angular/router';
import { routes } from './app.routes';
import { StockHistoryPageComponent } from './components/stock/stock-history-page.component';
import { AdminImportsComponent } from './components/admin/imports/admin-imports.component';
import { HistoricalCostRecoveryComponent } from './components/admin/historical-cost-recovery/historical-cost-recovery.component';
import { AvcoTransitionComponent } from './components/admin/avco-transition/avco-transition.component';
import { CostingRepairPageComponent } from './components/admin/costing-repair/costing-repair-page.component';

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

  /**
   * `/admin` keeps its own address rather than redirecting: the root header and the Dashboard
   * still link to it, and it is the only entry point to the dedicated Admin pages until the
   * navigation-shell task (#383) wires them into the sidebar.
   */
  it('keeps /admin as the Admin landing page rather than redirecting it', async () => {
    const admin = route('admin');
    expect(admin.redirectTo).toBeUndefined();
    expect(admin.canActivate).toContain(MsalGuard);
  });
});
