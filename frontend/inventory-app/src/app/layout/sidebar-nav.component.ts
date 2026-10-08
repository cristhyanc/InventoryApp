import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { Subject, filter, takeUntil } from 'rxjs';
import { IconComponent } from '../components/shared/icon.component';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { NavGroup, NavItem, NavLink, activeNavGroup, activeNavRoute, navigationFor } from './navigation';

/**
 * The application shell's left sidebar (issue #391). It renders the primary navigation, owns which
 * groups are expanded, and resolves which entry the current URL belongs to.
 *
 * It owns no layout decision: the shell says whether the labels are visible (`expanded`) and
 * whether this is a narrow-screen drawer (`drawer`), and the sidebar reports what the operator did
 * (`navigated`, `dismissed`, `expandRequested`) instead of reaching back into the shell.
 *
 * It owns no access decision either (issue #335). It starts with the navigation every operator
 * gets and adds the super-admin diagnostics link only when the diagnostics API itself confirms
 * platform-admin access, which is why the link is composed from `navigationFor` rather than
 * conditioned in this template.
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

  /** A destination was chosen, so a narrow drawer can be dismissed. */
  @Output() readonly navigated = new EventEmitter<void>();

  /** The drawer's own close control was used. */
  @Output() readonly dismissed = new EventEmitter<void>();

  /** A group heading was used while collapsed, which has no room for a submenu. */
  @Output() readonly expandRequested = new EventEmitter<void>();

  /** Starts without the diagnostics link: an unanswered probe must never show it. */
  navigation: readonly NavItem[] = navigationFor(false);

  activeRoute?: string;

  private readonly openGroups = new Set<string>();
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly router: Router,
    private readonly diagnosticsAccess: PlatformDiagnosticsAccessService
  ) {}

  ngOnInit(): void {
    this.applyActiveRoute(this.router.url);

    this.diagnosticsAccess
      .isGranted()
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (granted) => {
          this.navigation = navigationFor(granted);
          // A diagnostics page opened by URL before the probe answered becomes the active entry
          // once the link exists, so the Admin group reflects where the operator actually is.
          this.applyActiveRoute(this.router.url);
        },
        // Fail closed and silently: an unanswerable probe leaves the navigation everyone gets,
        // and a navigation menu is not the place to report that one capability could not be
        // checked.
        error: () => undefined
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

  isGroupActive(group: NavGroup): boolean {
    return group.children.some((child) => this.isLinkActive(child));
  }

  isGroupOpen(group: NavGroup): boolean {
    return this.openGroups.has(group.label);
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
}
