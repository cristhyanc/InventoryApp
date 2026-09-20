import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable, of, timer } from 'rxjs';
import { distinctUntilChanged, map, switchMap } from 'rxjs/operators';

@Injectable({ providedIn: 'root' })
export class LoadingService {
  private static readonly SHOW_DELAY_MS = 200;

  private readonly activeRequestCount$ = new BehaviorSubject<number>(0);

  readonly loading$: Observable<boolean> = this.activeRequestCount$.pipe(
    map((count) => count > 0),
    distinctUntilChanged(),
    switchMap((isActive) => (isActive ? timer(LoadingService.SHOW_DELAY_MS).pipe(map(() => true)) : of(false))),
    distinctUntilChanged()
  );

  startRequest(): void {
    this.activeRequestCount$.next(this.activeRequestCount$.value + 1);
  }

  endRequest(): void {
    this.activeRequestCount$.next(Math.max(0, this.activeRequestCount$.value - 1));
  }
}
