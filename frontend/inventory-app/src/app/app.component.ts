import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AuthenticationResult } from '@azure/msal-browser';
import { Subject, Subscription, catchError, defer, filter, map, of, retry, switchMap, take, takeUntil, timer } from 'rxjs';
import { ToastContainerComponent } from "./components/shared/toast-container.component";
import { IconComponent } from './components/shared/icon.component';
import { LoadingIndicatorComponent } from './components/shared/loading-indicator.component';
import { BreadcrumbsComponent } from './layout/breadcrumbs/breadcrumbs.component';
import { PRIMARY_NAVIGATION_ID, SidebarNavComponent } from './layout/sidebar-nav.component';
import { UserMenuComponent } from './layout/user-menu.component';
import { AccountStateService } from './access/account-state.service';
import { activeAccountKey, signedInAccountMayHaveChanged } from './access/signed-in-account';
import { BusinessService } from './services/business.service';
import { loginRequest } from './auth-config';

/**
 * The application shell (issue #391): a collapsible left sidebar for the primary navigation, a
 * header holding the signed-in user control, and the routed page.
 *
 * One `isSidebarOpen` flag drives both layouts, because the control an operator reaches for is the
 * same in each: on a wide layout the sidebar is always part of the page and the flag expands it to
 * labels or collapses it to a compact icon rail; on a narrow layout it is a dismissible overlay
 * drawer and the flag shows or hides it. `isWideLayout` comes from the same `lg` breakpoint the
 * Tailwind classes use, so the drawer is never left off-screen but focusable.
 *
 * Where that one control is rendered depends on the layout (issue #456), and only on the layout:
 * on a wide layout it is the sidebar's own header row beside the app title, reported back through
 * `collapseToggled`; on a narrow layout the sidebar is a drawer that is not on the page, so the
 * opener stays in this header and keeps the focus-return behaviour `closeSidebar` relies on. The
 * state, the handlers and every `aria` attribute are the ones this component already had.
 *
 * Authentication is unchanged: this component still owns the MSAL redirect handling, active
 * account and login/logout calls, and `UserMenuComponent` only reports the operator's intent.
 *
 * What the shell shows follows the identity signed in **now** (issue #604). It watches the same
 * `signedInAccountMayHaveChanged` trigger the access caches do - MSAL's account events as well as
 * `inProgress$` settling - so a direct Microsoft account switch, which raises
 * `ACTIVE_ACCOUNT_CHANGED` without any interaction, updates the displayed account, discards the
 * previous account's business context and published time zone, cancels its lookup, and closes the
 * routed-page gate until the new account's own business has answered. A superseded lookup can
 * neither publish nor open that gate; a sign-out clears the context with no authenticated call.
 */
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    RouterOutlet,
    SidebarNavComponent,
    UserMenuComponent,
    IconComponent,
    ToastContainerComponent,
    LoadingIndicatorComponent,
    BreadcrumbsComponent
  ],
  templateUrl: './app.component.html'
})
export class AppComponent implements OnInit, OnDestroy {
  private static readonly wideLayoutQuery = '(min-width: 1024px)';

  /**
   * Further attempts at the business lookup after a failed one, and the base delay between them
   * (issue #499). A transient failure at sign-in is retried while the page is still waiting, so a
   * single network blip does not leave every date on the page blank for the rest of the session.
   */
  static readonly businessLookupRetries = 2;
  static readonly businessLookupRetryDelayMs = 1000;

  title = 'Inventory Manager';

  isLoggedIn = false;
  userName = '';

  /** Expanded on a wide layout; the drawer is visible on a narrow one. */
  isSidebarOpen = true;
  isWideLayout = true;

  /** Whether the signed-in operator's business lookup has settled (issue #499). */
  private isBusinessSettled = false;
  private businessRequested = false;

  /**
   * The signed-in identity the business context above was requested for, or `null` when nobody is
   * signed in. A different identity means a different business, so the context is discarded and
   * read again rather than kept from the previous account. It is keyed by `activeAccountKey`, the
   * same identity the access caches use, so the two cannot disagree about who is signed in.
   */
  private businessAccountKey: string | null = null;
  private businessLookup?: Subscription;

  readonly sidebarId = PRIMARY_NAVIGATION_ID;

  /** Only rendered on a narrow layout, which is the only layout that returns focus to it. */
  @ViewChild('sidebarToggle') private readonly sidebarToggle?: ElementRef<HTMLButtonElement>;

  private wideLayout?: MediaQueryList;
  private readonly onWideLayoutChange = (event: MediaQueryListEvent): void => this.applyLayoutWidth(event.matches);
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService,
    private readonly accountState: AccountStateService,
    private readonly businessService: BusinessService
  ) {}

  /** The sidebar is only off the page when a narrow layout has dismissed its drawer. */
  get isSidebarVisible(): boolean {
    return this.isWideLayout || this.isSidebarOpen;
  }

  /** A narrow drawer always shows its labels; a wide sidebar shows them while it is expanded. */
  get isSidebarExpanded(): boolean {
    return !this.isWideLayout || this.isSidebarOpen;
  }

  get isSidebarDrawer(): boolean {
    return !this.isWideLayout;
  }

  /**
   * Whether the routed page may be rendered yet (issue #499). A signed-in operator's page is held
   * back until the business lookup has settled, because the business's time zone is what every
   * instant on that page is displayed in; a signed-out visitor sees the public landing page
   * immediately, as there is no business to read.
   */
  get isBusinessContextReady(): boolean {
    return !this.isLoggedIn || this.isBusinessSettled;
  }

  get sidebarToggleLabel(): string {
    if (this.isSidebarDrawer) {
      return this.isSidebarOpen ? 'Close navigation menu' : 'Open navigation menu';
    }
    return this.isSidebarOpen ? 'Collapse navigation' : 'Expand navigation';
  }

  ngOnInit(): void {
    this.observeLayoutWidth();

    this.authService.handleRedirectObservable()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (result: AuthenticationResult | null) => {
          if (result?.account) {
            this.authService.instance.setActiveAccount(result.account);
          }
          this.updateLoginState();
        },
        error: (error) => {
          console.error('Authentication redirect failed', error);
        }
      });

    // Whoever is signed in now, from both signals that report it changing (issue #604): MSAL's own
    // account events - `ACTIVE_ACCOUNT_CHANGED` among them - and `inProgress$` settling, which is
    // what says any authentication interaction has finished and the accounts may be inspected. A
    // direct account switch raises only the event, because no interaction is involved: the shell
    // that watched the interaction status alone therefore kept the previous account's name,
    // business and time zone on screen for the rest of the session.
    signedInAccountMayHaveChanged(this.msalBroadcastService)
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => {
        this.ensureActiveAccount();
        this.updateLoginState();
      });
  }

  ngOnDestroy(): void {
    this.wideLayout?.removeEventListener('change', this.onWideLayoutChange);
    this.destroying$.next();
    this.destroying$.complete();
  }

  toggleSidebar(): void {
    this.isSidebarOpen = !this.isSidebarOpen;
  }

  expandSidebar(): void {
    this.isSidebarOpen = true;
  }

  closeSidebar(): void {
    this.isSidebarOpen = false;
    this.sidebarToggle?.nativeElement.focus();
  }

  /** A narrow drawer covers the page, so choosing a destination dismisses it. */
  onSidebarNavigated(): void {
    if (this.isSidebarDrawer) {
      this.isSidebarOpen = false;
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.isSidebarDrawer && this.isSidebarOpen) {
      this.closeSidebar();
    }
  }

  login(): void {
    this.authService.loginRedirect({ ...loginRequest });
  }

  logout(): void {
    const account = this.authService.instance.getActiveAccount();
    this.authService.logoutRedirect({
      account: account ?? undefined,
      postLogoutRedirectUri: window.location.origin
    });
  }

  private observeLayoutWidth(): void {
    const query = window.matchMedia?.(AppComponent.wideLayoutQuery);
    if (!query) {
      // No media-query support: keep the wide default, which shows the navigation rather than
      // hiding it behind a drawer the operator cannot be told about.
      return;
    }

    this.wideLayout = query;
    this.applyLayoutWidth(query.matches);
    query.addEventListener('change', this.onWideLayoutChange);
  }

  private applyLayoutWidth(isWide: boolean): void {
    this.isWideLayout = isWide;
    // A wide layout opens with labels; a narrow one starts with the drawer dismissed so it never
    // covers the page the operator asked for.
    this.isSidebarOpen = isWide;
  }

  private ensureActiveAccount(): void {
    if (this.authService.instance.getActiveAccount()) {
      return;
    }

    const [firstAccount] = this.authService.instance.getAllAccounts();
    if (firstAccount) {
      this.authService.instance.setActiveAccount(firstAccount);
    }
  }

  private updateLoginState(): void {
    const account = this.authService.instance.getActiveAccount();
    this.isLoggedIn = account !== null;
    this.userName = account?.name ?? account?.username ?? '';

    // The same identity the access caches key their answers on, so what the shell shows and what
    // they answer cannot drift apart across an account switch (issue #604).
    const accountKey = activeAccountKey(this.authService);
    if (accountKey !== this.businessAccountKey) {
      // A different account (or a sign-out) owns a different business context. Drop the previous
      // one entirely - the cached lookup, the published zone and any lookup still in flight - so
      // neither the loading state nor a failure can show the previous business's zone, and read
      // the business again as the new account. The routed page goes back to waiting: it is on
      // screen only while the business whose zone it renders its instants in is the current
      // account's own.
      this.businessAccountKey = accountKey;
      this.businessLookup?.unsubscribe();
      this.businessLookup = undefined;
      this.businessService.reset();
      this.businessRequested = false;
      this.isBusinessSettled = false;
    }

    if (this.isLoggedIn && !this.businessRequested) {
      // The business's own name and time zone, which every operator-facing date/time is rendered
      // in (issue #499). It is an authenticated read, so it cannot happen at bootstrap; it is
      // requested here, once sign-in has settled or the signed-in account has changed. The routed
      // page waits for it (see isBusinessContextReady), because a page rendered before the
      // business is known would show its instants in no time zone at all.
      //
      // It waits for the account state first (issue #529): a person who is not `Member` has no
      // business to look up, and `GET /api/business/current` would only answer the same `403`
      // `BusinessScopeMiddleware` already gives every other member endpoint. An account state that
      // could not be read is treated the same way `accountStateGuard` treats it - the lookup is
      // attempted anyway, because the membership check on the endpoint itself is the real boundary.
      //
      // The answer it waits for is the **current** identity's (issue #604). `AccountStateService`
      // publishes `unread` again from the moment the identity changes (issue #603) and the shell
      // starts this lookup afresh on the same signal, so the one answer that can decide the new
      // account's business is the new account's own; `take(1)` ends this subscription on it, and a
      // later identity change replaces the whole subscription rather than repairing it.
      this.businessRequested = true;
      this.businessLookup = this.accountState.state$
        .pipe(
          filter((answer) => answer.status !== 'unread'),
          take(1),
          switchMap((answer) => {
            if (answer.status === 'known' && answer.accountState.state !== 'Member') {
              return of(null);
            }

            return defer(() => this.businessService.load()).pipe(
              // A failed lookup emits null and is retryable (BusinessService drops it), so it is
              // asked again, with a growing delay, before the shell gives up on it.
              map((business) => {
                if (business === null) {
                  throw new Error('The current business could not be read.');
                }
                return business;
              }),
              retry({
                count: AppComponent.businessLookupRetries,
                delay: (_error, attempt) => timer(attempt * AppComponent.businessLookupRetryDelayMs)
              }),
              catchError(() => of(null))
            );
          }),
          takeUntil(this.destroying$)
        )
        .subscribe(() => {
          // Settled either way. A lookup that still failed after its retries published no zone,
          // so dates render as unavailable - but the application is still shown rather than
          // waiting forever. Only the current identity's lookup reaches this: a superseded one was
          // unsubscribed above, so a response that arrives late can neither mark this context
          // ready nor publish the previous business's zone (`BusinessService.reset`).
          this.isBusinessSettled = true;
        });
    }
  }
}
