import { CanDeactivateFn } from '@angular/router';

/** A routed page that can hold work a departure would lose, and asks before it is lost. */
export interface PendingWorkGuarded {
  /** `true` to leave; `false` keeps the operator on the page with every piece of state intact. */
  confirmLeave(): boolean;
}

/**
 * The Pick List's `canDeactivate` guard (issue #996): an in-app navigation away from typed refill
 * quantities, edited or checked planning work, or an unconfirmed refill save asks first. A reload
 * or a closed tab is the browser's own `beforeunload` prompt, which the page raises itself.
 */
export const confirmPickListLeave: CanDeactivateFn<PendingWorkGuarded> = (component) => component?.confirmLeave() ?? true;
