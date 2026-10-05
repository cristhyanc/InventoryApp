import { MsalGuard } from '@azure/msal-angular';
import { Route } from '@angular/router';
import { routes } from './app.routes';
import { StockHistoryPageComponent } from './components/stock/stock-history-page.component';
import { AdminImportsComponent } from './components/admin/imports/admin-imports.component';

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
