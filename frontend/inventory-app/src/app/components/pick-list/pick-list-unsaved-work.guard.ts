import { CanDeactivateFn } from '@angular/router';
import { PickListComponent } from './pick-list.component';

/**
 * Guards in-app navigation away from the Pick List while it holds work leaving would lose (issue
 * #996): quantities entered but not recorded, planning edits and checked picks, or - most
 * seriously - a refill submission whose outcome is unresolved, whose retry context lives only in
 * that component.
 *
 * It owns no rule of its own: `PickListComponent.confirmLeave` is the one place the question is
 * asked and the one place the sentences live, so the route guard and the page's own
 * `beforeunload` handler cannot drift apart. Asking changes nothing, so a cancelled departure
 * leaves every entry, override, mark and kept submission exactly as it was.
 */
export const pickListUnsavedWorkGuard: CanDeactivateFn<PickListComponent> = (component) => component.confirmLeave();
