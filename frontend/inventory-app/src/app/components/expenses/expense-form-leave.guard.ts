import { CanDeactivateFn } from '@angular/router';
import { PendingWorkGuarded } from '../pick-list/pick-list-leave.guard';

/**
 * Add and Edit expense's `canDeactivate` guard (issue #1167), following the New/Edit Product
 * pattern: an in-app departure - Cancel, Back to expenses, the navigation - from changes that are
 * not saved yet asks first. A reload or a closed tab is the browser's own `beforeunload` prompt,
 * which the page raises itself.
 */
export const confirmExpenseFormLeave: CanDeactivateFn<PendingWorkGuarded> = (component) =>
  component?.confirmLeave() ?? true;
