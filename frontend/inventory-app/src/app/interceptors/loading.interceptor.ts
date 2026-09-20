import { inject } from '@angular/core';
import { HttpInterceptorFn } from '@angular/common/http';
import { finalize } from 'rxjs/operators';
import { LoadingService } from '../services/loading.service';

/**
 * Requests under /assets/ are static/runtime-config assets (e.g. config.json fetched
 * before the app config is available) and must not drive the API loading overlay.
 */
export function isTrackedByLoadingIndicator(url: string): boolean {
  return !url.startsWith('/assets/') && !url.includes('/assets/');
}

export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  const loadingService = inject(LoadingService);

  if (!isTrackedByLoadingIndicator(req.url)) {
    return next(req);
  }

  loadingService.startRequest();
  return next(req).pipe(finalize(() => loadingService.endRequest()));
};
