import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

export const productsLegacyRouteGuard: CanActivateFn = (route) => {
  const router = inject(Router);
  const reorderView = route.queryParamMap.get('reorderView');
  const lowStockOnly = route.queryParamMap.get('lowStockOnly');

  let target: string | null = null;
  if (reorderView === 'needs' || lowStockOnly === 'true') {
    target = '/products/needs-ordering';
  } else if (reorderView === 'onOrder') {
    target = '/products/on-order';
  } else if (reorderView === 'all') {
    target = '/products';
  }

  if (!target) {
    return true;
  }

  const queryParams = { ...route.queryParams };
  delete queryParams['reorderView'];
  delete queryParams['lowStockOnly'];

  return router.createUrlTree([target], { queryParams });
};
