import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { Subject, catchError, combineLatest, filter, of, startWith, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { CapabilitySet } from '../access/capabilities';
import { IconComponent } from '../components/shared/icon.component';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { SuperAdminBusinessesService } from '../services/super-admin-businesses.service';
import { NavGroup, NavItem, NavLink, activeNavGroup, activeNavRoute, navigationFor, superAdminNavGroup } from './navigation';

/**
 * The id of the navigation landmark. It lives here because the `<nav>` that carries it is this
 * component's own element, and both of the shell's toggles point `aria-controls` at it.
 */
export const PRIMARY_NAVIGATION_ID = 'primary-navigation';

/**
 * The application shell's left sidebar (issue #391). It renders the primary navigation, owns which
 * groups are expanded, and resolves which entry the current URL belongs to.
 *
 * It owns no layout decision: the shell says whether the labels are visible (`expanded`) and
 * whether this is a narrow-screen drawer (`drawer`), and the sidebar reports what the operator did
 * (`navigated`, `dismissed`, `expandRequested`, `collapseToggled`) instead of reaching back into
 * the shell.
 *
 * Its header row holds the desktop collapse control (issue #456). The control moved here from the
 * shell's top bar so it sits beside the app title instead of floating above the menu, but it still
 * only reports the intent: the shell keeps the one `isSidebarOpen` flag that decides what
 * `expanded` means on each layout.
 *
 * It owns no access decision (issues #335 and #528). The destinations it renders come from
 * `navigationFor`, which is given the capability set `GET /api/me/access` reported for the
 * signed-in member and the diagnostics API's separate answer about platform-admin access; this
 * component decides neither and conditions nothing in its template. Until the capability set has
 * been read it renders no destination at all, because a menu that offered everything and then
 * removed what the role cannot use would show entries that were never this member's.
 *
 * The host renders as `display: contents` (issue #455) so it contributes no box of its own: the
 * shell's `items-stretch` row needs to stretch the actual white `<nav>` panel to the row's full
 * height, and a host element rendering as an ordinary block would absorb that stretched height
 * instead of passing it to its single child, leaving the visible panel sized to its content.
 */
@Component({
  selector: 'app-sidebar-nav',
  standalone: true,
  imports: [RouterLink, IconComponent],
  host: { class: 'contents' },
  templateUrl: './sidebar-nav.component.html'
})
export class SidebarNavComponent implements OnInit, OnDestroy {
  /** Wide screens collapse to a compact icon rail; a narrow drawer is always expanded. */
  @Input() expanded = true;

  /** True while the sidebar is the narrow-screen overlay drawer rather than part of the layout. */
  @Input() drawer = false;

  /** The id the shell's `aria-controls` points at; it is set on the `<nav>` landmark itself. */
  @Input() navId = PRIMARY_NAVIGATION_ID;

  /** A destination was chosen, so a narrow drawer can be dismissed. */
  @Output() readonly navigated = new EventEmitter<void>();

  /** The drawer's own close control was used. */
  @Output() readonly dismissed = new EventEmitter<void>();

  /** A group heading was used while collapsed, which has no room for a submenu. */
  @Output() readonly expandRequested = new EventEmitter<void>();

  /** The header row's collapse/expand control was used (issue #456). */
  @Output() readonly collapseToggled = new EventEmitter<void>();

  /**
   * Starts empty: no destination is offered before the member's capabilities are known, and the
   * diagnostics link is never shown on an unanswered probe.
   */
  navigation: readonly NavItem[] = [];

  activeRoute?: string;

  /**
   * How many business applications are waiting for a decision (issue #547), shown as a badge on
   * the `Super Admin` group and its `Businesses` child. `null` until the platform-admin probe
   * grants access and the count itself is known, which is also what a failed read leaves it as:
   * the Businesses page's own table is the authority on refusing to show a wrong number, and this
   * badge simply shows nothing rather than a stale or guessed one.
   */
  pendingBusinessCount: number | null = null;

  private readonly openGroups = new Set<string>();
  private readonly destroying$ = new Subject<void>();
  private hasSuperAdminAccess = false;

  constructor(
    private readonly router: Router,
    private readonly access: AccessService,
    private readonly diagnosticsAccess: PlatformDiagnosticsAccessService,
    private readonly superAdminBusinesses: SuperAdminBusinessesService
  ) {}

  ngOnInit(): void {
    this.applyActiveRoute(this.router.url);

    // The diagnostics probe starts as a refusal and the menu is not held back for it: it answers
    // a different question, on its own schedule, and an unanswerable probe must leave the menu
    // the member's capabilities already decided. Both failures are silent - a navigation menu is
    // not the place to report that an access check could not be made.
    //
    // `granted$` keeps following the signed-in identity (issue #605) rather than answering once:
    // it reports `false` again the moment the identity changes, so a previous identity's granted
    // `Super Admin` group is removed on the switch itself and reappears only if the new identity's
    // own probe confirms access.
    const hasDiagnosticsAccess = this.diagnosticsAccess.granted$.pipe(
      catchError(() => of(false)),
      startWith(false)
    );

    combineLatest([this.access.access$, hasDiagnosticsAccess])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([access, diagnosticsGranted]) => {
        this.navigation = navigationFor(renderedCapabilities(access), diagnosticsGranted);
        // A page opened by URL before the answers arrived becomes the active entry once its link
        // exists, so the owning group reflects where the operator actually is.
        this.applyActiveRoute(this.router.url);

        this.hasSuperAdminAccess = diagnosticsGranted;
        if (diagnosticsGranted) {
          this.refreshPendingBusinessCount();
        } else {
          // No access means no rendered group to show it beside, and a previous identity's count
          // must not survive the switch (issue #605's same rule for the group itself).
          this.pendingBusinessCount = null;
        }
      });

    // The Businesses page (issue #547) reports every approve, reject, deactivate, reactivate and
    // release here instead of this component reading its state directly, so the badge agrees
    // with that page's own list without either one polling the other.
    this.superAdminBusinesses.changed$.pipe(takeUntil(this.destroying$)).subscribe(() => {
      if (this.hasSuperAdminAccess) {
        this.refreshPendingBusinessCount();
      }
    });

    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntil(this.destroying$)
      )
      .subscribe((event) => this.applyActiveRoute(event.urlAfterRedirects));
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  isLinkActive(link: NavLink): boolean {
    return this.activeRoute === link.route;
  }

  /** Whether `item`'s badge is the pending-business-applications count (issue #547). */
  isSuperAdminGroup(item: NavItem): boolean {
    return item.kind === 'group' && item.label === superAdminNavGroup.label;
  }

  isBusinessesLink(link: NavLink): boolean {
    return link.route === '/super-admin/businesses';
  }

  isGroupActive(group: NavGroup): boolean {
    return group.children.some((child) => child.kind === 'link' && this.isLinkActive(child));
  }

  isGroupOpen(group: NavGroup): boolean {
    return this.openGroups.has(group.label);
  }

  /** The header control's accessible name names the action, not the current state (issue #456). */
  get collapseToggleLabel(): string {
    return this.expanded ? 'Collapse navigation' : 'Expand navigation';
  }

  groupPanelId(group: NavGroup): string {
    return `sidebar-group-${group.label.toLowerCase().replace(/\s+/g, '-')}`;
  }

  toggleGroup(group: NavGroup): void {
    if (!this.expanded) {
      // A compact rail cannot show a submenu, so the heading means "give me the labels back".
      this.openGroups.add(group.label);
      this.expandRequested.emit();
      return;
    }

    if (this.openGroups.has(group.label)) {
      this.openGroups.delete(group.label);
    } else {
      this.openGroups.add(group.label);
    }
  }

  private applyActiveRoute(url: string): void {
    this.activeRoute = activeNavRoute(this.navigation, url);

    // The active page's group stays visible; a group the operator closed reopens only when one of
    // its own pages is opened again.
    const group = activeNavGroup(this.navigation, url);
    if (group) {
      this.openGroups.add(group.label);
    }
  }

  private refreshPendingBusinessCount(): void {
    this.superAdminBusinesses
      .pendingCount()
      .pipe(
        catchError(() => of(null)),
        takeUntil(this.destroying$)
      )
      .subscribe((count) => (this.pendingBusinessCount = count));
  }
}

const NO_CAPABILITIES: CapabilitySet = new Set();

/**
 * The capability set to render the menu from, for each of the three things the access read can
 * say:
 *
 * - **granted** - the member's own capabilities, which is the whole point;
 * - **unread** - nothing yet. An empty set offers no destination, so the menu fills in once the
 *   answer arrives rather than offering every entry and then taking some away;
 * - **unavailable** - `undefined`, which offers every destination. The capability set is not
 *   known, the API refuses what the role may not do regardless, and an empty menu would strand a
 *   member whose capability read happened to fail (see `AccessService`).
 */
function renderedCapabilities(state: MemberAccessState): CapabilitySet | undefined {
  if (state.status === 'granted') {
    return state.access.capabilities;
  }

  return state.status === 'unread' ? NO_CAPABILITIES : undefined;
}
