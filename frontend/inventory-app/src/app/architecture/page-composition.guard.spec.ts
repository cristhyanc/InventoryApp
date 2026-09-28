import * as fs from 'fs';
import * as path from 'path';
import {
  hasInlinePageDialogMarkup,
  listRoutedPageComponentModules,
  resolveComponentTemplateSource
} from './page-composition.guard';

describe('page composition guard', () => {
  it('accepts a page template that composes a distinct workflow through a dedicated child component', () => {
    const acceptedTemplate = `
      <h1>Machine detail</h1>
      <app-machine-restock-sync
        [machineId]="machine?.machineID"
        (restockApplied)="refreshProducts()">
      </app-machine-restock-sync>
    `;

    expect(hasInlinePageDialogMarkup(acceptedTemplate)).toBe(false);
  });

  it('rejects a page template that reintroduces its own dialog markup instead of a child component', () => {
    const regressedTemplate = `
      <h1>Machine detail</h1>
      @if (syncDialogOpen) {
        <div class="fixed inset-0" role="dialog" aria-modal="true">
          <h2>Restock sync</h2>
          <button type="button" (click)="applySync()">Apply</button>
        </div>
      }
    `;

    expect(hasInlinePageDialogMarkup(regressedTemplate)).toBe(true);
  });

  it('lists every routed page component module from both lazy and eagerly loaded routes', () => {
    const routesSource = `
      import { Routes } from '@angular/router';
      import { AuthCallbackComponent } from './auth/auth-callback.component';

      export const routes: Routes = [
        { path: 'auth', component: AuthCallbackComponent },
        {
          path: '',
          loadComponent: () => import('./components/dashboard/dashboard.component').then((m) => m.DashboardComponent)
        },
        {
          path: 'products',
          loadComponent: () => import('./components/products/products-shell.component').then((m) => m.ProductsShellComponent),
          children: [
            {
              path: '',
              loadComponent: () => import('./components/products/product-list.component').then((m) => m.ProductListComponent)
            }
          ]
        }
      ];
    `;

    expect(listRoutedPageComponentModules(routesSource)).toEqual(
      expect.arrayContaining([
        './auth/auth-callback.component',
        './components/dashboard/dashboard.component',
        './components/products/products-shell.component',
        './components/products/product-list.component'
      ])
    );
  });

  describe('every routed page component in this application', () => {
    const appDir = path.resolve(__dirname, '..');
    const routesSource = fs.readFileSync(path.resolve(appDir, 'app.routes.ts'), 'utf8');
    const modulePaths = listRoutedPageComponentModules(routesSource);

    it('found the routes currently declared in app.routes.ts', () => {
      expect(modulePaths.length).toBeGreaterThan(10);
    });

    it.each(modulePaths)('%s does not author dialog markup directly in its own template', (modulePath: string) => {
      const componentDir = path.resolve(appDir, path.dirname(modulePath));
      const componentTsPath = path.resolve(appDir, `${modulePath}.ts`);
      const componentSource = fs.readFileSync(componentTsPath, 'utf8');
      const templateSource = resolveComponentTemplateSource(componentSource, componentDir);

      expect(hasInlinePageDialogMarkup(templateSource)).toBe(false);
    });
  });
});
