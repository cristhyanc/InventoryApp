import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { BehaviorSubject, Subject, catchError, combineLatest, distinctUntilChanged, filter, of, startWith, switchMap, takeUntil } from 'rxjs';
import { AccessService } from '../access/access.service';
import { BusinessNavigationVisibility } from '../access/business-navigation';
import { Entitlement } from '../access/entitlement';
import { IconComponent } from '../components/shared/icon.component';
import { ProviderAvailability, ProviderAvailabilityService } from '../provider/provider-availability.service';
import { NavGroup, NavItem, NavLink, activeNavGroup, activeNavRoute, navLockOf, navLockSummary, navigationFor } from './navigation';
import { renderedCapabilities, renderedEntitlement, renderedOptionVisibility } from './sidebar-nav.component';

/** One shortcut on the bottom bar: a destination the member's own menu already offers. */
export interface BottomNavShortcut {
  readonly label: string;
  readonly route: string;
  readonly icon: string;
  /** The top-level entry the shortcut stands for, which decides whether it is the active one. */
  readonly source: NavLink | NavGroup;
}

/**
 * The top-level entries the bottom bar offers a shortcut to, in order (issue #972, from the
 * owner-approved Restockroo mobile design): Dashboard, Pick List, Sites and the Inventory group.
 * They are named by route or group label, never re-declared, so the bar can only ever offer an
 * entry `navigationFor` already rendered for this member - with its own label, capability and
 * provider lock.
 */
const SHORTCUT_ROUTES: readonly string[] = ['/', '/pick-list', '/sites'];
const SHORTCUT_GROUP = 'Inventory';

/**
 * The narrow layout's bottom navigation bar (issue #972): up to four shortcuts and **More**, which
 * opens the full menu as a sheet (the sidebar's drawer mode).
 *
 * It owns no access or provider decision. Its shortcuts are picked out of the very same
 * `navigationFor` result the sidebar renders, from the same capability set, option visibility and
 * shared provider answer, so a shortcut is offered only when the member's menu already offers that
 * destination, and a locked one is marked exactly as the menu marks it. The platform-admin probe is
 * not asked: no shortcut is a Super Admin destination, and that group stays in the More sheet.
 * Until the capability set has been read no shortcut is rendered, for the same reason the sidebar
 * renders no destination yet; More is always offered, because the sheet is the whole menu.
 */
@Component({
  selector: 'app-bottom-nav',
  standalone: true,
  imports: [RouterLink, IconComponent],
  template: `
    <nav
      aria-label="Shortcuts"
      class="fixed inset-x-0 bottom-0 z-30 border-t border-rr-line bg-white font-rr pb-[env(safe-area-inset-bottom)]"
      data-testid="bottom-nav"
    >
      <ul class="m-0 flex list-none items-stretch justify-around p-0">
        @for (shortcut of shortcuts; track shortcut.route) {
          <li class="min-w-0 flex-1">
            <a
              [routerLink]="shortcut.route"
              class="flex min-h-14 flex-col items-center justify-center gap-0.5 px-1 py-1.5 text-xs font-semibold no-underline"
              [class.text-rr-teal]="isActive(shortcut)"
              [class.text-rr-muted]="!isActive(shortcut)"
              [attr.aria-current]="isActive(shortcut) ? 'page' : null"
              [attr.data-testid]="'bottom-nav-' + shortcut.route"
            >
              <app-icon [name]="shortcut.icon" variant="outlined" [size]="22" />
              <span class="max-w-full truncate">{{ shortcut.label }}</span>
              @if (lockSummary(shortcut); as summary) {
                <span class="sr-only">{{ summary }}</span>
              }
            </a>
          </li>
        }
        <li class="min-w-0 flex-1">
          <button
            type="button"
            class="flex min-h-14 w-full flex-col items-center justify-center gap-0.5 px-1 py-1.5 text-xs font-semibold text-rr-muted"
            aria-haspopup="dialog"
            [attr.aria-expanded]="moreOpen"
            [attr.aria-controls]="moreOpen ? menuId : null"
            data-testid="bottom-nav-more"
            (click)="moreRequested.emit($any($event.currentTarget))"
          >
            <app-icon name="menu" variant="outlined" [size]="22" />
            <span>More</span>
          </button>
        </li>
      </ul>
    </nav>
  `
})
export class BottomNavComponent implements OnInit, OnDestroy {
  /** Whether the More sheet is open, which the shell owns. */
  @Input() moreOpen = false;

  /** The id of the menu the More control opens, for `aria-controls`. */
  @Input() menuId = '';

  private readonly businessNavigation$ = new BehaviorSubject<BusinessNavigationVisibility>('withheld');

  /** The shell's answer about whether this account state has a business menu (issue #676). */
  @Input()
  set businessNavigation(visibility: BusinessNavigationVisibility) {
    this.businessNavigation$.next(visibility);
  }

  /** More was pressed; the control itself is passed so the shell can return focus to it. */
  @Output() readonly moreRequested = new EventEmitter<HTMLElement>();

  shortcuts: readonly BottomNavShortcut[] = [];

  private navigation: readonly NavItem[] = [];
  private provider?: ProviderAvailability;
  private entitlement?: Entitlement;
  private activeRoute?: string;
  private activeGroup?: string;
  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly router: Router,
    private readonly access: AccessService,
    private readonly providerAvailability: ProviderAvailabilityService
  ) {}

  ngOnInit(): void {
    const visibility$ = this.businessNavigation$.pipe(distinctUntilChanged());
    const provider$ = visibility$.pipe(
      switchMap((visibility) =>
        visibility === 'offered'
          ? this.providerAvailability.watch().pipe(
              catchError(() => of(undefined)),
              startWith(undefined)
            )
          : of(undefined)
      )
    );

    combineLatest([this.access.access$, visibility$, provider$])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([access, visibility, provider]) => {
        this.provider = provider;
        this.entitlement = renderedEntitlement(access);
        this.navigation = navigationFor(
          renderedCapabilities(access),
          false,
          visibility,
          provider,
          renderedOptionVisibility(access)
        );
        this.shortcuts = shortcutsOf(this.navigation);
        this.applyActiveRoute(this.router.url);
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

  isActive(shortcut: BottomNavShortcut): boolean {
    return shortcut.source.kind === 'group'
      ? this.activeGroup === shortcut.source.label
      : this.activeRoute === shortcut.source.route;
  }

  /** The menu's own lock sentence for a locked shortcut, or `''` for an open one. */
  lockSummary(shortcut: BottomNavShortcut): string {
    if (shortcut.source.kind !== 'link') {
      return '';
    }
    return navLockSummary(navLockOf(shortcut.source, this.entitlement, this.provider), shortcut.source);
  }

  private applyActiveRoute(url: string): void {
    this.activeRoute = activeNavRoute(this.navigation, url);
    this.activeGroup = activeNavGroup(this.navigation, url)?.label;
  }
}

/**
 * The shortcuts out of a member's rendered menu. The Inventory group's shortcut opens its first
 * destination this member is offered - Products for every role that has the group today.
 */
export function shortcutsOf(navigation: readonly NavItem[]): readonly BottomNavShortcut[] {
  const shortcuts: BottomNavShortcut[] = [];
  for (const route of SHORTCUT_ROUTES) {
    const entry = navigation.find((item): item is NavLink => item.kind === 'link' && item.route === route);
    if (entry !== undefined) {
      shortcuts.push({ label: entry.label, route: entry.route, icon: entry.icon ?? '', source: entry });
    }
  }

  const group = navigation.find((item): item is NavGroup => item.kind === 'group' && item.label === SHORTCUT_GROUP);
  const first = group?.children.find((child): child is NavLink => child.kind === 'link');
  if (group !== undefined && first !== undefined) {
    shortcuts.push({ label: group.label, route: first.route, icon: group.icon, source: group });
  }
  return shortcuts;
}
