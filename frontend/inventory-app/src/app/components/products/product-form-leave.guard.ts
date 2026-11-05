import { CanDeactivateFn } from '@angular/router';
import { PendingWorkGuarded } from '../pick-list/pick-list-leave.guard';

/**
 * New and Edit Product's `canDeactivate` guard (issue #1111): an in-app departure - Cancel, Back to
 * Products, the navigation - from changes that are not saved yet asks first. A reload or a closed tab
 * is the browser's own `beforeunload` prompt, which the page raises itself.
 */
export const confirmProductFormLeave: CanDeactivateFn<PendingWorkGuarded> = (component) =>
  component?.confirmLeave() ?? true;
