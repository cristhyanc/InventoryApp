import { ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Location, ViewportScroller } from '@angular/common';
import { CanActivateFn, Router, RouterOutlet, Scroll, provideRouter, withInMemoryScrolling } from '@angular/router';
import { filter, firstValueFrom, throwError, timeout } from 'rxjs';
import { ScrollOnNavigationService } from './scroll-on-navigation.service';

@Component({ standalone: true, selector: 'app-scroll-test-root', imports: [RouterOutlet], template: '<router-outlet></router-outlet>' })
class RootComponent {}

@Component({ standalone: true, template: 'page a' })
class PageAComponent {}

@Component({ standalone: true, template: 'page b' })
class PageBComponent {}

@Component({ standalone: true, template: 'fragment page' })
class FragmentPageComponent {}

/**
 * The router's deferred `Scroll` event, handled centrally (issue #1048), on the real router with a
 * spied `ViewportScroller` - exactly the contract `app.config.ts` wires up with
 * `withInMemoryScrolling({ scrollPositionRestoration: 'disabled', anchorScrolling: 'disabled' })`.
 *
 * The `Scroll` event is only emitted once `RouterScroller.init()` has run, which Angular does from
 * an `APP_BOOTSTRAP_LISTENER` fired by `ApplicationRef.bootstrap` - not by `TestBed.createComponent`
 * alone, which is all `RouterTestingHarness` does. These tests therefore bootstrap a real root
 * component the same way `bootstrapApplication` does, so the router drives the same deferred
 * `Scroll` event production code depends on, rather than a synthetic stand-in for it.
 *
 * Every step is awaited on that event rather than on a delay (see `awaitingScroll`). A fixed timer
 * cannot be right here: a Back or a Forward runs through jsdom's own `popstate` task, the router's
 * navigation and only then the `setTimeout`-deferred `Scroll`, and a wait that expired before the
 * end of that chain made this file fail intermittently under load - "expected [0, 640], received
 * [0, 0]", which is a restoration that had not happened yet rather than a handler that scrolled to
 * the top.
 */
describe('ScrollOnNavigationService (issue #1048)', () => {
  let scrollToPosition: jest.Mock;
  let scrollToAnchor: jest.Mock;
  let router: Router;
  let blockedPath: string | null;
  /**
   * Where the window is scrolled to right now. The router reads this through
   * `ViewportScroller.getScrollPosition()` when a navigation starts, which is what it later hands
   * back as the `Scroll` event's `position` on Back/Forward - so a test that wants to prove real
   * restoration has to move it to a nonzero value first, exactly as an operator scrolling the page
   * does. A fixed `[0, 0]` here would make restoration indistinguishable from scrolling to the top.
   */
  let scrollPosition: [number, number];
  /** The element the test's root component is bootstrapped into; removed again after each test. */
  let host: HTMLElement | null = null;

  const blockingGuard: CanActivateFn = (_route, state) => {
    if (blockedPath !== null && state.url === blockedPath) {
      return false;
    }
    return true;
  };

  /**
   * How long a `Scroll` event the router owes us may take to arrive before the test gives up and
   * says so. It is a safety net rather than a wait: `awaitingScroll` resolves on the event itself,
   * so this bound is reached only when the router produced no event at all - and a test that said
   * "expected [0, 640], received [0, 0]" because a fixed delay expired first is exactly the failure
   * this file no longer has.
   */
  const scrollEventTimeoutMs = 2000;

  /**
   * The next deferred `Scroll` event the router delivers (see `RouterScroller`, which schedules it
   * with `setTimeout` after the navigation it belongs to has ended).
   *
   * Subscribing is what makes this deterministic, and it must happen *before* the action that
   * produces the event: the subscription is in place for the whole async chain a Back or Forward
   * runs through - jsdom's own `popstate` task, the router's navigation, `NavigationEnd` and finally
   * the deferred event - however long that chain takes under load. `ScrollOnNavigationService` has
   * already handled the event when this promise's continuation runs, because RxJS notifies every
   * subscriber synchronously and an awaited promise resumes in a later microtask.
   */
  function nextScrollEvent(): Promise<Scroll> {
    return firstValueFrom(
      router.events.pipe(
        filter((event): event is Scroll => event instanceof Scroll),
        timeout({
          first: scrollEventTimeoutMs,
          with: () =>
            throwError(
              () =>
                new Error(
                  `The router delivered no Scroll event within ${scrollEventTimeoutMs} ms of the navigation.`
                )
            )
        })
      )
    );
  }

  /** Runs `act` and resolves once the `Scroll` event it produces has been handled. */
  async function awaitingScroll(act: () => unknown): Promise<void> {
    const scrolled = nextScrollEvent();
    await act();
    await scrolled;
  }

  /**
   * Settles a navigation that produces **no** `Scroll` event at all - the one a guard cancels, which
   * reaches neither `NavigationEnd` nor the same-URL `NavigationSkipped` the router turns into one.
   * There is no event to await, so this waits the one macrotask turn `RouterScroller` defers by:
   * had the router scheduled an event, it would have run by now.
   */
  async function flushPendingScrollEvent(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }

  async function navigate(url: string): Promise<void> {
    await awaitingScroll(() => router.navigateByUrl(url));
  }

  /** The operator scrolling the page they are on, before they navigate away from it. */
  function scrollTo(position: [number, number]): void {
    scrollPosition = position;
  }

  async function goBack(): Promise<void> {
    await awaitingScroll(() => TestBed.inject(Location).back());
  }

  async function goForward(): Promise<void> {
    await awaitingScroll(() => TestBed.inject(Location).forward());
  }

  async function harness(): Promise<void> {
    scrollToPosition = jest.fn();
    scrollToAnchor = jest.fn();
    blockedPath = null;
    scrollPosition = [0, 0];

    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: 'a', component: PageAComponent, canActivate: [blockingGuard] },
            { path: 'b', component: PageBComponent, canActivate: [blockingGuard] },
            { path: 'fragment', component: FragmentPageComponent },
            { path: '**', redirectTo: 'a' }
          ],
          withInMemoryScrolling({ scrollPositionRestoration: 'disabled', anchorScrolling: 'disabled' })
        ),
        {
          provide: ViewportScroller,
          useValue: {
            getScrollPosition: () => scrollPosition,
            scrollToPosition,
            scrollToAnchor,
            setOffset: jest.fn(),
            setHistoryScrollRestoration: jest.fn()
          }
        }
      ]
    });

    host = document.createElement('app-scroll-test-root');
    document.body.appendChild(host);

    // Starts the handler, exactly as `provideEnvironmentInitializer` does in `app.config.ts`, and
    // before the bootstrap below so it sees that navigation's own event - which is also what lets
    // the baseline be awaited rather than assumed.
    TestBed.inject(ScrollOnNavigationService);
    router = TestBed.inject(Router);

    // The initial navigation the bootstrap triggers, settled to a known baseline path.
    await awaitingScroll(() => TestBed.inject(ApplicationRef).bootstrap(RootComponent, host));

    scrollToPosition.mockClear();
    scrollToAnchor.mockClear();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    host?.remove();
    host = null;
  });

  it('scrolls to the top on a successful navigation to a different primary path', async () => {
    await harness();
    await navigate('/a');
    scrollTo([0, 640]);
    scrollToPosition.mockClear();

    await navigate('/b');

    expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);
  });

  it('restores the nonzero position the history entry had on Back', async () => {
    await harness();
    await navigate('/a');
    scrollTo([0, 640]);
    // Leaving page A is what stores [0, 640] against page A's history entry, and page B then opens
    // at the top - so a restoration to [0, 640] can only come from that stored position.
    await navigate('/b');
    scrollTo([0, 0]);
    scrollToPosition.mockClear();

    await goBack();

    expect(scrollToPosition).toHaveBeenCalledWith([0, 640]);
    expect(scrollToPosition).not.toHaveBeenCalledWith([0, 0]);
    expect(scrollToAnchor).not.toHaveBeenCalled();
  });

  it('restores the position that forward history entry had on Forward', async () => {
    await harness();
    await navigate('/a');
    scrollTo([0, 640]);
    await navigate('/b');
    // A different position on page B, so Forward cannot pass by restoring page A's one.
    scrollTo([0, 215]);
    await goBack();
    expect(scrollToPosition).toHaveBeenCalledWith([0, 640]);
    scrollTo([0, 640]);
    scrollToPosition.mockClear();

    await goForward();

    expect(scrollToPosition).toHaveBeenCalledWith([0, 215]);
    expect(scrollToPosition).not.toHaveBeenCalledWith([0, 0]);
    expect(scrollToPosition).not.toHaveBeenCalledWith([0, 640]);
    expect(scrollToAnchor).not.toHaveBeenCalled();
  });

  it('scrolls to the fragment element instead of the top', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await navigate('/fragment#details');

    expect(scrollToAnchor).toHaveBeenCalledWith('details');
    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll a query-param-only update on the same primary path', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await navigate('/a?open=1');

    expect(scrollToPosition).not.toHaveBeenCalled();
    expect(scrollToAnchor).not.toHaveBeenCalled();
  });

  it('does not scroll a replaceUrl navigation on the same primary path', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await awaitingScroll(() =>
      router.navigate(['/a'], { queryParams: { open: '1' }, replaceUrl: true })
    );

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll re-navigating to the current URL with onSameUrlNavigation: reload', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await awaitingScroll(() => router.navigate(['/a'], { onSameUrlNavigation: 'reload' }));

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll re-clicking the current page (default ignored same-URL navigation)', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await navigate('/a');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not move the page for a navigation a guard cancels', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();
    blockedPath = '/b';

    // A cancelled navigation reaches no `NavigationEnd`, so there is no `Scroll` event to await.
    await router.navigateByUrl('/b');
    await flushPendingScrollEvent();

    expect(scrollToPosition).not.toHaveBeenCalled();
    expect(scrollToAnchor).not.toHaveBeenCalled();
  });
});
