import { ChangeDetectorRef, Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AuthenticationResult } from '@azure/msal-browser';
import { Subject, Subscription, catchError, defer, filter, map, of, retry, switchMap, take, takeUntil, timer } from 'rxjs';
import packageJson from '../../package.json';
import { ToastContainerComponent } from "./components/shared/toast-container.component";
import { IconComponent } from './components/shared/icon.component';
import { LoadingIndicatorComponent } from './components/shared/loading-indicator.component';
import { BreadcrumbsComponent } from './layout/breadcrumbs/breadcrumbs.component';
import { PRIMARY_NAVIGATION_ID, SidebarNavComponent } from './layout/sidebar-nav.component';
import { SupportSessionBannerComponent } from './layout/support-session-banner.component';
import { SupportSessionBlockedComponent } from './layout/support-session-blocked.component';
import { UserMenuComponent } from './layout/user-menu.component';
import { AccountStateAnswer, AccountStateService } from './access/account-state.service';
import { BusinessNavigationVisibility, businessNavigationFor } from './access/business-navigation';
import { activeAccountKey, signedInAccountMayHaveChanged } from './access/signed-in-account';
import { BusinessService } from './services/business.service';
import { SupportSessionService } from './support-session/support-session.service';
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
 * **Which account states have a business menu at all is decided here** (issue #676). The sidebar
 * renders the capability set `GET /api/me/access` reported, whose unreadable answer deliberately
 * offers every destination; only the shell knows the three things that tell a confirmed member
 * whose probe failed apart from somebody with no usable membership - who is signed in, what
 * `GET /api/me/account-state` answered for them, and whether this tab is in an effective support
 * view - so it passes the sidebar `businessNavigation` ({@link businessNavigationFor}) and the
 * sidebar renders it. The sidebar itself stays on the page either way, because the separately
 * authorized `Super Admin` group is independent of business membership.
 *
 * What the shell shows follows the identity signed in **now** (issue #604). It watches the same
 * `signedInAccountMayHaveChanged` trigger the access caches do - MSAL's account events as well as
 * `inProgress$` settling - so a direct Microsoft account switch, which raises
 * `ACTIVE_ACCOUNT_CHANGED` without any interaction, updates the displayed account, discards the
 * previous account's business context and published time zone, cancels its lookup, and closes the
 * routed-page gate until the new account's own business has answered. A superseded lookup can
 * neither publish nor open that gate; a sign-out clears the context with no authenticated call.
 *
 * **A tab that cannot present the administrator's support session renders no page at all**
 * (issue #668). `SupportSessionService` publishes `{ kind: 'unresolved' }` for a reloaded or newly
 * opened tab that learned a session is running but holds no id for it; such a tab's requests would
 * resolve the administrator's **own** membership, so the shell shows
 * `SupportSessionBlockedComponent`'s explanation and its **End session** action in place of the
 * routed page - no routed page means no business-scoped request a page would make - while the
 * "Viewing as" banner stays off. An effective support view (`viewing`) and ordinary context
 * (`inactive`) both render the page exactly as before.
 *
 * **A support-context transition takes the page off screen before the new context loads**
 * (issue #670). `SupportSessionService.contextChanging$` is `true` while a start, end, expiry,
 * refusal or cross-tab notification is settling, and the shell withholds the routed page for
 * exactly that window - flushing the removal there and then, so the previous business's figures
 * leave with its banner rather than at the end of the navigation. Withholding the page is also
 * what *replaces* it: the component holding those figures is destroyed, and the one created when
 * the window closes reads the effective context from scratch, which a navigation to the URL the
 * tab is already on could never do on its own.
 *
 * **The business context is read again whenever the effective business changes** (issue #671). The
 * once-per-account lookup above is not enough on its own: a support start, end or expiry drops the
 * previous context's published zone (`BusinessService.reset`) without reading the new one, and an
 * accepted invite or an approved application turns the identity that is **already** signed in into a
 * `Member` after that one lookup has settled with no business at all. Both re-read it now -
 * `SupportSessionService.contextChanged$` for the first, the account state's own
 * nonmember-to-`Member` transition for the second - so what is on screen is the effective business's
 * own zone rather than no zone at all. A support transition withholds the page until the new zone
 * has answered, because that page's instants are rendered in it; a membership transition does not,
 * because the page on screen is an onboarding page with no business instant on it that is in the
 * middle of the navigation acceptance or approval performs, and destroying it would strand the
 * operator there. A lookup that cannot be answered says so, with a retry
 * ({@link isBusinessUnavailable}), instead of leaving every instant blank with no explanation.
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
    BreadcrumbsComponent,
    SupportSessionBannerComponent,
    SupportSessionBlockedComponent
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

  /** The build's own package version (issue #648), read from `package.json` rather than duplicated. */
  readonly appVersion = packageJson.version;

  isLoggedIn = false;
  userName = '';

  /** Expanded on a wide layout; the drawer is visible on a narrow one. */
  isSidebarOpen = true;
  isWideLayout = true;

  /** Whether the signed-in operator's business lookup has settled (issue #499). */
  private isBusinessSettled = false;
  private businessRequested = false;

  /**
   * Whether the settled lookup could not read the business at all (issue #671), which is the one
   * state the shell used to keep to itself: no zone was published, so every instant on the page
   * rendered blank with nothing saying why and nothing to press. It is deliberately not set for an
   * account that simply has no business to look up - there is nothing wrong there - and it is
   * cleared the moment a new lookup is made.
   */
  isBusinessUnavailable = false;

  /**
   * Whether the lookup was deliberately not made because the account state said this account is not
   * a member of any business (issue #529). It is what tells an accepted invite or an approved
   * application apart from any other account-state answer: the same identity becoming a `Member`
   * then has a business to read that nothing has read (issue #671).
   */
  private isBusinessSkippedForNonMember = false;

  /**
   * Whether this tab is in an **effective** support view (issue #671). The business its requests
   * resolve is then the session's own, so the account-state skip above must not apply to the
   * lookup: a platform administrator may hold no membership at all, and `GET /api/business/current`
   * in a session answers for the business being viewed.
   */
  private isSupportViewEffective = false;

  /**
   * Whether a support session is active for this administrator that this tab cannot present
   * (issue #668), which is what withholds the routed page. It is `SupportSessionService`'s own
   * `unresolved` state and nothing else: the shell neither decides it nor tries to repair it, and
   * it takes precedence over the business-context gate below - what is wrong is not that the
   * business is unknown but that any page rendered here would be the administrator's own data for
   * a session that names somebody else's business.
   */
  isSupportViewUnresolved = false;

  /**
   * Whether a business-context transition is still settling (issue #670), which is the other reason
   * the routed page is withheld. It is `SupportSessionService.contextChanging$` and nothing the
   * shell decides: the service owns when a context changes, and the shell owns what that does to
   * what is on screen.
   */
  private isBusinessContextChanging = false;

  /**
   * The signed-in identity the business context above was requested for, or `null` when nobody is
   * signed in. A different identity means a different business, so the context is discarded and
   * read again rather than kept from the previous account. It is keyed by `activeAccountKey`, the
   * same identity the access caches use, so the two cannot disagree about who is signed in.
   */
  private businessAccountKey: string | null = null;
  private businessLookup?: Subscription;

  /**
   * The cached account-state answer for the identity signed in **now** (issue #676), which is what
   * {@link businessNavigation} is decided from. It is dropped back to `unread` by an identity
   * change here as well as by `AccountStateService` itself, so the menu cannot be built from the
   * previous account's answer for however long the new read takes, whichever of the two is told
   * about the change first.
   */
  private accountStateAnswer: AccountStateAnswer = { status: 'unread' };

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
    private readonly businessService: BusinessService,
    private readonly supportSession: SupportSessionService,
    private readonly changeDetector: ChangeDetectorRef
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
    return !this.isLoggedIn || (this.isBusinessSettled && !this.isBusinessContextChanging);
  }

  /**
   * Whether the sidebar offers the ordinary business navigation (issue #676), decided here because
   * the shell is what knows who is signed in, what their account state answered and whether this
   * tab is in an effective support view - see {@link businessNavigationFor} for the rule itself.
   *
   * The sidebar is still rendered either way: the separately authorized `Super Admin` group is
   * independent of business membership (issue #546), so a platform administrator who holds no
   * membership of their own keeps that group while the business menu is withheld.
   */
  get businessNavigation(): BusinessNavigationVisibility {
    return businessNavigationFor({
      isSignedIn: this.isLoggedIn,
      accountState: this.accountStateAnswer,
      isSupportViewEffective: this.isSupportViewEffective
    });
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

    // Whether this tab may render a business page at all (issue #668). Subscribing is also what
    // starts the service's one-time own-session check, exactly as the banner's subscription does.
    this.supportSession.state$
      .pipe(takeUntil(this.destroying$))
      .subscribe((state) => {
        this.isSupportViewUnresolved = state.kind === 'unresolved';
        // Which business the next lookup would be answered for (issue #671), kept here rather than
        // asked for inside the lookup so it is already the new one when a transition reports.
        this.isSupportViewEffective = state.kind === 'viewing';
      });

    // What a changed business context does to the page on screen (issue #670).
    this.supportSession.contextChanging$
      .pipe(takeUntil(this.destroying$))
      .subscribe((changing) => this.applyBusinessContextChange(changing));

    // The effective business itself changed (issue #671), which is a different question from
    // whether its transition is still settling: the zone the previous context published has been
    // dropped, and the business now in view has its own. The page waits for it, exactly as it waits
    // for the first lookup of a signed-in account.
    this.supportSession.contextChanged$
      .pipe(takeUntil(this.destroying$))
      .subscribe(() => this.requestBusinessContext(true));

    // A nonmember account that becomes a `Member` (issue #671): an accepted invite (#535) and an
    // approved application (#542) both publish that here, after the shell's own lookup has already
    // settled with no business. The page is **not** withheld for it - see `applyAccountState`.
    this.accountState.state$
      .pipe(takeUntil(this.destroying$))
      .subscribe((answer) => this.applyAccountState(answer));
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
    // A support session is the signed-in administrator's, so signing out ends it first (issue #681)
    // rather than leaving it running on the server, and presented by other open tabs, for the rest
    // of its lifetime. It always completes, so a failed end never blocks the sign-out.
    this.supportSession.endBeforeSignOut().subscribe(() =>
      this.authService.logoutRedirect({
        account: account ?? undefined,
        postLogoutRedirectUri: window.location.origin
      })
    );
  }

  /**
   * Reads the business again after a lookup that could not be answered (issue #671), from the retry
   * the shell offers beside that explanation. It withholds the page while it runs, because the
   * operator asked for it: the loading state is the honest answer to a control they just pressed,
   * and the page rebuilt afterwards is one whose instants have the zone.
   */
  retryBusinessContext(): void {
    this.requestBusinessContext(true);
  }

  /**
   * Withholds the routed page while a business context transition settles, and renders the removal
   * **immediately** (issue #670).
   *
   * The flush is the point. A transition is reported from a response callback or a
   * `BroadcastChannel` message, and the navigation it performs can settle in the same turn - a tab
   * already on `/` has nothing to navigate - so a flag set and cleared before Angular's next change
   * detection would never take the page off screen at all, and the previous business's figures
   * would stay exactly where they were. Flushing here destroys that page component the moment the
   * context changes, which is also what makes the page created when the transition finishes a new
   * one that reads the effective context from scratch.
   *
   * It is only ever flushed on the way *in*: releasing the page is Angular's ordinary change
   * detection, and a flush during the shell's own first render would be re-entrant.
   */
  private applyBusinessContextChange(changing: boolean): void {
    if (changing === this.isBusinessContextChanging) {
      return;
    }

    this.isBusinessContextChanging = changing;
    if (changing) {
      this.changeDetector.detectChanges();
    }
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
      this.isBusinessUnavailable = false;
      this.isBusinessSkippedForNonMember = false;
      // The previous account's account state goes with it (issue #676): the navigation it decided
      // was that person's, and the new identity has answered nothing yet.
      this.accountStateAnswer = { status: 'unread' };
    }

    if (this.isLoggedIn && !this.businessRequested) {
      this.requestBusinessContext(true);
    }
  }

  /**
   * Reads the **effective** business and publishes its time zone, which every operator-facing
   * date/time on the page is rendered in (issue #499).
   *
   * It is an authenticated read, so it cannot happen at bootstrap. It is made once sign-in has
   * settled, again whenever the signed-in account changes (issue #604), and again whenever the
   * business this tab resolves changes under the same identity (issue #671): a support start, end or
   * expiry, and the account state becoming `Member`.
   *
   * It waits for the account state first (issue #529): a person who is not `Member` has no business
   * to look up, and `GET /api/business/current` would only answer the same `403`
   * `BusinessScopeMiddleware` already gives every other member endpoint. An account state that could
   * not be read is treated the same way `accountStateGuard` treats it - the lookup is attempted
   * anyway, because the membership check on the endpoint itself is the real boundary. Inside an
   * effective support view the skip does not apply at all (issue #671): the business the request
   * resolves is the session's, not this administrator's own membership, which they may not have.
   *
   * The answer it waits for is the **current** identity's (issue #604). `AccountStateService`
   * publishes `unread` again from the moment the identity changes (issue #603) and this lookup is
   * started afresh on the same signal, so the one answer that can decide the new account's business
   * is the new account's own; `take(1)` ends the subscription on it, and every later request
   * replaces the whole subscription rather than repairing it - which is also what keeps a superseded
   * response from readying this context or publishing the previous business's zone.
   *
   * `withholdThePage` says whether the routed page waits for the answer. It does for the first
   * lookup of an account and for a support transition, whose page renders instants in the zone being
   * read. It deliberately does not for a membership transition: the page on screen then is an
   * onboarding page with no business instant on it, in the middle of the navigation acceptance or
   * approval performs, and destroying it would tear that navigation down and strand the operator
   * there.
   */
  private requestBusinessContext(withholdThePage: boolean): void {
    this.businessLookup?.unsubscribe();
    this.businessRequested = true;
    this.isBusinessUnavailable = false;
    this.isBusinessSkippedForNonMember = false;
    if (withholdThePage) {
      this.isBusinessSettled = false;
    }

    this.businessLookup = this.accountState.state$
      .pipe(
        filter((answer) => answer.status !== 'unread'),
        take(1),
        switchMap((answer) => {
          if (answer.status === 'known' && answer.accountState.state !== 'Member' && !this.isSupportViewEffective) {
            this.isBusinessSkippedForNonMember = true;
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
      .subscribe((business) => {
        // Settled either way, so the application is shown rather than waiting forever. A lookup
        // that still failed after its retries published no zone - dates stay unavailable, never the
        // browser's or another business's - and the shell now says so and offers the retry
        // (issue #671) instead of leaving the page to render blanks silently. An account with no
        // business to look up is not that case and is not reported as one.
        this.isBusinessSettled = true;
        this.isBusinessUnavailable = business === null && !this.isBusinessSkippedForNonMember;
      });
  }

  /**
   * Reads the business when the account state reports the same identity has become a `Member`
   * (issue #671) - an accepted invite (#535) or an approved application (#542) - and the lookup this
   * context already made was skipped because that account held no membership. Nothing else about an
   * account-state answer concerns the business context: an identity *change* is handled by
   * `updateLoginState`, which drops the previous context first, and a `Member` answer for a context
   * that already read its business is the answer that read it.
   *
   * Every answer is kept, though, because it is also what decides whether the sidebar offers the
   * business navigation at all (issue #676, {@link businessNavigation}).
   */
  private applyAccountState(answer: AccountStateAnswer): void {
    this.accountStateAnswer = answer;

    if (!this.isBusinessSkippedForNonMember || !this.isLoggedIn) {
      return;
    }

    if (answer.status !== 'known' || answer.accountState.state !== 'Member') {
      return;
    }

    this.requestBusinessContext(false);
  }
}
