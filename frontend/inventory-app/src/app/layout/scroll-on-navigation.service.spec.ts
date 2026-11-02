import { ApplicationRef, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Location, ViewportScroller } from '@angular/common';
import { CanActivateFn, Router, RouterOutlet, provideRouter, withInMemoryScrolling } from '@angular/router';
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
 */
describe('ScrollOnNavigationService (issue #1048)', () => {
  let scrollToPosition: jest.Mock;
  let scrollToAnchor: jest.Mock;
  let router: Router;
  let blockedPath: string | null;

  const blockingGuard: CanActivateFn = (_route, state) => {
    if (blockedPath !== null && state.url === blockedPath) {
      return false;
    }
    return true;
  };

  /** Lets the real, `setTimeout`-deferred `Scroll` event (see `RouterScroller`) run. */
  async function flushScrollEvent(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }

  async function navigate(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await flushScrollEvent();
  }

  async function harness(): Promise<void> {
    scrollToPosition = jest.fn();
    scrollToAnchor = jest.fn();
    blockedPath = null;

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
            getScrollPosition: () => [0, 0],
            scrollToPosition,
            scrollToAnchor,
            setOffset: jest.fn(),
            setHistoryScrollRestoration: jest.fn()
          }
        }
      ]
    });

    const host = document.createElement('app-scroll-test-root');
    document.body.appendChild(host);
    TestBed.inject(ApplicationRef).bootstrap(RootComponent, host);
    // Starts the handler, exactly as `provideEnvironmentInitializer` does in `app.config.ts`.
    TestBed.inject(ScrollOnNavigationService);
    router = TestBed.inject(Router);

    // The initial navigation the bootstrap above triggers, settled to a known baseline path.
    await flushScrollEvent();
    scrollToPosition.mockClear();
    scrollToAnchor.mockClear();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('scrolls to the top on a successful navigation to a different primary path', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await navigate('/b');

    expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);
  });

  it('restores the stored position on Back', async () => {
    await harness();
    await navigate('/a');
    await navigate('/b');
    scrollToPosition.mockClear();

    TestBed.inject(Location).back();
    await flushScrollEvent();

    expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);
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

    await router.navigate(['/a'], { queryParams: { open: '1' }, replaceUrl: true });
    await flushScrollEvent();

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll re-navigating to the current URL with onSameUrlNavigation: reload', async () => {
    await harness();
    await navigate('/a');
    scrollToPosition.mockClear();

    await router.navigate(['/a'], { onSameUrlNavigation: 'reload' });
    await flushScrollEvent();

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

    await navigate('/b');

    expect(scrollToPosition).not.toHaveBeenCalled();
    expect(scrollToAnchor).not.toHaveBeenCalled();
  });
});
