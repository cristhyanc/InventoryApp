import { Injectable } from '@angular/core';
import { Observable, Subject } from 'rxjs';

/**
 * Keeps at most one popover open at a time (issue #1029): a product search list or a help tip
 * announces itself as it opens, and every other popover listening closes. A native `<select>` needs
 * none of this, because the browser already closes its list when focus moves.
 *
 * It carries only the identity of whoever opened, never any state of their own.
 */
@Injectable({ providedIn: 'root' })
export class PopoverCoordinator {
  private readonly openedSubject = new Subject<object>();

  /** Each popover's owner as it opens. A listener closes itself when the owner is not itself. */
  readonly opened$: Observable<object> = this.openedSubject.asObservable();

  announceOpen(owner: object): void {
    this.openedSubject.next(owner);
  }
}
