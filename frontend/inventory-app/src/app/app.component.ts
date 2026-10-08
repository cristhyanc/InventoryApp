import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { AuthenticationResult, InteractionStatus } from '@azure/msal-browser';
import { Subject, filter, takeUntil } from 'rxjs';
import { ToastContainerComponent } from "./components/shared/toast-container.component";
import { IconComponent } from './components/shared/icon.component';
import { LoadingIndicatorComponent } from './components/shared/loading-indicator.component';
import { PRIMARY_NAVIGATION_ID, SidebarNavComponent } from './layout/sidebar-nav.component';
import { UserMenuComponent } from './layout/user-menu.component';
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
    LoadingIndicatorComponent
  ],
  templateUrl: './app.component.html'
})
export class AppComponent implements OnInit, OnDestroy {
  private static readonly wideLayoutQuery = '(min-width: 1024px)';

  title = 'Inventory Manager';

  isLoggedIn = false;
  userName = '';

  /** Expanded on a wide layout; the drawer is visible on a narrow one. */
  isSidebarOpen = true;
  isWideLayout = true;

  readonly sidebarId = PRIMARY_NAVIGATION_ID;

  /** Only rendered on a narrow layout, which is the only layout that returns focus to it. */
  @ViewChild('sidebarToggle') private readonly sidebarToggle?: ElementRef<HTMLButtonElement>;

  private wideLayout?: MediaQueryList;
  private readonly onWideLayoutChange = (event: MediaQueryListEvent): void => this.applyLayoutWidth(event.matches);
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly authService: MsalService,
    private readonly msalBroadcastService: MsalBroadcastService
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

    // Do not inspect accounts until MSAL has finished any authentication interaction.
    this.msalBroadcastService.inProgress$
      .pipe(
        filter((status: InteractionStatus) => status === InteractionStatus.None),
        takeUntil(this.destroying$)
      )
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
  }
}
