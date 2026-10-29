import { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { PickListComponent } from './pick-list.component';
import { pickListUnsavedWorkGuard } from './pick-list-unsaved-work.guard';

function runGuard(component: PickListComponent): boolean {
  const snapshot = {} as ActivatedRouteSnapshot;
  const state = {} as RouterStateSnapshot;
  return pickListUnsavedWorkGuard(component, snapshot, state, state) as boolean;
}

/**
 * The guard owns no rule of its own (issue #996): it asks the page, so the route and the browser's
 * own reload prompt cannot disagree about what leaving would lose.
 */
describe('pickListUnsavedWorkGuard', () => {
  it('leaves when the page says there is nothing to lose', () => {
    const confirmLeave = jest.fn(() => true);

    expect(runGuard({ confirmLeave } as unknown as PickListComponent)).toBe(true);
    expect(confirmLeave).toHaveBeenCalledTimes(1);
  });

  it('stays when the page refuses', () => {
    expect(runGuard({ confirmLeave: () => false } as unknown as PickListComponent)).toBe(false);
  });
});
