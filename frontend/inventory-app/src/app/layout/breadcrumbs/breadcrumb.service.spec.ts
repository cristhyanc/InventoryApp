import { TestBed } from '@angular/core/testing';
import { NavigationEnd, NavigationStart, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { take } from 'rxjs/operators';
import { BreadcrumbService } from './breadcrumb.service';

function setup(): { service: BreadcrumbService; events: Subject<unknown> } {
  const events = new Subject<unknown>();

  TestBed.configureTestingModule({
    providers: [BreadcrumbService, { provide: Router, useValue: { events, url: '/' } }]
  });

  return { service: TestBed.inject(BreadcrumbService), events };
}

async function firstValue<T>(obs: { pipe: (...args: unknown[]) => unknown }): Promise<T> {
  return new Promise((resolve) => {
    (obs as unknown as { subscribe: (next: (value: T) => void) => void }).subscribe(resolve);
  });
}

describe('BreadcrumbService (issue #457)', () => {
  it('starts with no live label', async () => {
    const { service } = setup();

    await expect(firstValue(service.currentPageLabel$.pipe(take(1)))).resolves.toBeNull();
  });

  it('reports a label a routed page sets from its own already-loaded data', async () => {
    const { service } = setup();

    service.setCurrentPageLabel('Snack Attack 42');

    await expect(firstValue(service.currentPageLabel$.pipe(take(1)))).resolves.toBe('Snack Attack 42');
  });

  it('clears the label on every navigation, so a reused component never shows a stale name', async () => {
    const { service, events } = setup();
    service.setCurrentPageLabel('Snack Attack 42');

    events.next(new NavigationStart(1, '/machines/9'));

    await expect(firstValue(service.currentPageLabel$.pipe(take(1)))).resolves.toBeNull();
  });

  it('ignores a NavigationEnd: only NavigationStart resets the label', async () => {
    const { service, events } = setup();
    service.setCurrentPageLabel('Snack Attack 42');

    events.next(new NavigationEnd(1, '/machines/9', '/machines/9'));

    await expect(firstValue(service.currentPageLabel$.pipe(take(1)))).resolves.toBe('Snack Attack 42');
  });
});
