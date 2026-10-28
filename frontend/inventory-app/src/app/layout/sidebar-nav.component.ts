import { NgClass, NgTemplateOutlet } from '@angular/common';
import { AfterViewInit, Component, ElementRef, EventEmitter, Input, OnDestroy, OnInit, Output, ViewChild } from '@angular/core';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import {
  BehaviorSubject,
  Subject,
  catchError,
  combineLatest,
  distinctUntilChanged,
  filter,
  of,
  startWith,
  switchMap,
  takeUntil
} from 'rxjs';
import { AccessService, MemberAccessState } from '../access/access.service';
import { BusinessNavigationVisibility } from '../access/business-navigation';
import { CapabilitySet } from '../access/capabilities';
import { OptionVisibility } from '../access/option-visibility';
import { IconComponent } from '../components/shared/icon.component';
import { ProviderAvailability, ProviderAvailabilityService } from '../provider/provider-availability.service';
import { ProviderLock, providerLockSummary } from '../provider/provider-lock';
import { PlatformDiagnosticsAccessService } from '../services/platform-diagnostics-access.service';
import { SuperAdminBusinessesService } from '../services/super-admin-businesses.service';
import {
  NavGroup,
  NavItem,
  NavLink,
  NavSectionHeading,
  activeNavGroup,
  activeNavRoute,
  navLockOf,
  navigationFor,
  optionSwitchedOff,
  superAdminNavGroup
} from './navigation';

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
 * It owns no access decision (issues #335, #528 and #676). The destinations it renders come from
 * `navigationFor`, which is given the capability set `GET /api/me/access` reported for the
 * signed-in member, the diagnostics API's separate answer about platform-admin access, and the
 * shell's own `businessNavigation` answer about whether this account state has a business menu at
 * all; this component decides none of the three and conditions nothing in its template. Until the
 * capability set has been read it renders no destination at all, because a menu that offered
 * everything and then removed what the role cannot use would show entries that were never this
 * member's.
 *
 * It owns no provider decision either (issue #729). Whether a live provider destination is locked,
 * and whether the provider repair tools are offered at all, comes from the shared availability
 * signal of issue #728 through `navigationFor` and `navLockOf`; this component subscribes to that
 * one shared answer and renders it, and goes on rendering it for as long as it is mounted, so a
 * connection made, lost or switched while the operator is on a page updates the menu there and
 * then instead of waiting for the next full page load. Unlike the capability set the menu is
 * **not** held back for it: nothing is locked until the answer settles, because an unread status
 * is not a refusal, and a withheld business menu asks for no provider answer at all - there is no
 * business to ask about.
 *
 * The host renders as `display: contents` (issue #455) so it contributes no box of its own: the
 * shell's `items-stretch` row needs to stretch the actual white `<nav>` panel to the row's full
 * height, and a host element rendering as an ordinary block would absorb that stretched height
 * instead of passing it to its single child, leaving the visible panel sized to its content.
 */
@Component({
  selector: 'app-sidebar-nav',
  standalone: true,
  imports: [RouterLink, IconComponent, NgClass, NgTemplateOutlet],
  host: { class: 'contents' },
  templateUrl: './sidebar-nav.component.html'
})
export class SidebarNavComponent implements OnInit, AfterViewInit, OnDestroy {
  /** Wide screens collapse to a compact icon rail; a narrow drawer is always expanded. */
  @Input() expanded = true;

  /** True while the sidebar is the narrow-screen overlay drawer rather than part of the layout. */
  @Input() drawer = false;

  /** The id the shell's `aria-controls` points at; it is set on the `<nav>` landmark itself. */
  @Input() navId = PRIMARY_NAVIGATION_ID;

  private readonly businessNavigation$ = new BehaviorSubject<BusinessNavigationVisibility>('withheld');

  /**
   * Whether the ordinary business destinations are on offer at all (issue #676), which the shell
   * decides from the authenticated account state and this component only renders - the same
   * division of labour the layout inputs above follow. It defaults to `withheld`, so a host that
   * does not say leaves the business menu out rather than offering it to somebody the account
   * state was never consulted about; the separately authorized `Super Admin` group is unaffected.
   */
  @Input()
  set businessNavigation(visibility: BusinessNavigationVisibility) {
    this.businessNavigation$.next(visibility);
  }

  private readonly rolePreviewActive$ = new BehaviorSubject(false);

  /**
   * Whether the Super Admin is previewing a business role (issue #721), which withholds the
   * `Super Admin` group and its badge: no business role is offered that group, so a preview that
   * kept it would be showing the one part of the menu the previewed role cannot see. Like
   * {@link businessNavigation} it is the shell's answer and this component only renders it, and it
   * defaults to "not previewing" so a host that does not say changes nothing.
   */
  @Input()
  set rolePreviewActive(active: boolean) {
    this.rolePreviewActive$.next(active);
  }

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

  private sheet?: HTMLDialogElement;

  /**
   * The More sheet is a native `<dialog>` opened with `showModal()` (issue #972), so the browser
   * itself makes everything behind it inert: no click, programmatic focus or assistive-technology
   * navigation reaches the page while the sheet is open. It is opened the moment it is rendered,
   * which is whenever the shell shows the menu on a narrow layout.
   */
  @ViewChild('sheet')
  private set sheetElement(ref: ElementRef<HTMLDialogElement> | undefined) {
    this.sheet = ref?.nativeElement;
    if (this.sheet !== undefined && !this.sheet.open) {
      openModal(this.sheet);
    }
  }

  @ViewChild('closeControl') private readonly closeControl?: ElementRef<HTMLButtonElement>;

  private readonly openGroups = new Set<string>();

  /** The groups the More sheet has opened once already, so one the operator closes stays closed. */
  private readonly sheetGroupsOpened = new Set<string>();
  private readonly destroying$ = new Subject<void>();
  private hasSuperAdminAccess = false;

  /** The settled provider answer, or `undefined` while nothing is known about it (issue #729). */
  private provider?: ProviderAvailability;

  /** The access answers the menu is built from, once they have been read at all. */
  private menuInputs?: {
    readonly capabilities: CapabilitySet | undefined;
    readonly diagnosticsOffered: boolean;
    readonly businessNavigation: BusinessNavigationVisibility;
    readonly options: OptionVisibility | undefined;
  };

  constructor(
    private readonly router: Router,
    private readonly access: AccessService,
    private readonly diagnosticsAccess: PlatformDiagnosticsAccessService,
    private readonly superAdminBusinesses: SuperAdminBusinessesService,
    private readonly providerAvailability: ProviderAvailabilityService
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

    combineLatest([
      this.access.access$,
      hasDiagnosticsAccess,
      this.businessNavigation$.pipe(distinctUntilChanged()),
      this.rolePreviewActive$.pipe(distinctUntilChanged())
    ])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([access, diagnosticsGranted, businessNavigation, rolePreviewActive]) => {
        // A preview withholds the `Super Admin` group exactly as a refused probe does (issue #721):
        // what the previewed role is shown is the business menu its own capabilities allow, and
        // that group belongs to no business role at all.
        const diagnosticsOffered = diagnosticsGranted && !rolePreviewActive;
        this.menuInputs = {
          capabilities: renderedCapabilities(access),
          diagnosticsOffered,
          businessNavigation,
          options: renderedOptionVisibility(access)
        };
        this.renderNavigation();

        this.hasSuperAdminAccess = diagnosticsOffered;
        if (diagnosticsOffered) {
          this.refreshPendingBusinessCount();
        } else {
          // No access means no rendered group to show it beside, and a previous identity's count
          // must not survive the switch (issue #605's same rule for the group itself).
          this.pendingBusinessCount = null;
        }
      });

    // The shared provider answer (issue #729), asked for only while this account state has a
    // business menu at all: a signed-out visitor and a known nonmember resolve no business, so
    // there is no connection to read a status for. It starts `undefined` so the menu renders
    // immediately and locks nothing until the answer settles, and a failed read settles as
    // `unknown`, which locks nothing either (issue #728's fail-open rule).
    //
    // It is deliberately its own subscription rather than another arm of the `combineLatest` above:
    // the provider answer changes what the menu *renders*, and nothing else - the platform-admin
    // probe and the pending-applications count answer different questions, and re-asking either one
    // because a connection status settled would be one extra request per page load.
    //
    // It `watch`es rather than `load`s because this menu outlives the answer: an Owner who connects
    // Nayax in Integrations, loses the credentials, or starts a support session on another business
    // never leaves this component, so a menu built from the answer read at mount would go on
    // locking what now works - and disagreeing with `requireProvider`, which asks again on every
    // navigation. Watching costs no extra probe (see `ProviderAvailabilityService.watch`).
    this.businessNavigation$
      .pipe(
        distinctUntilChanged(),
        switchMap((visibility) =>
          visibility === 'offered'
            ? this.providerAvailability.watch().pipe(
                catchError(() => of(undefined)),
                startWith(undefined)
              )
            : of(undefined)
        ),
        takeUntil(this.destroying$)
      )
      .subscribe((provider) => {
        this.provider = provider;
        this.renderNavigation();
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

  /**
   * The More sheet takes focus as it opens (issue #972): it is a modal dialog over the page, so the
   * keyboard starts inside it, on the Close control at its top. The shell returns focus to whatever
   * opened it when it closes.
   */
  ngAfterViewInit(): void {
    if (this.drawer) {
      this.closeControl?.nativeElement.focus();
    }
  }

  ngOnDestroy(): void {
    // Leave the top layer before the element goes, so the page is interactive again at once.
    if (this.sheet?.open) {
      closeModal(this.sheet);
    }
    this.destroying$.next();
    this.destroying$.complete();
  }

  /**
   * The dialog's own close request (Escape, or a platform back gesture). The browser would close
   * the dialog by itself, behind the shell's back; it is cancelled and reported instead, so the
   * shell closes the menu and returns focus to the control that opened it.
   */
  onSheetCancel(event: Event): void {
    event.preventDefault();
    this.dismissed.emit();
  }

  /**
   * A click whose target is the dialog element itself landed on its `::backdrop` - the dimmed page
   * above the sheet - because the sheet's content fills the dialog box. That dismisses the sheet.
   */
  onSheetClick(event: MouseEvent): void {
    if (event.target === this.sheet) {
      this.dismissed.emit();
    }
  }

  /**
   * Keeps Tab and Shift+Tab cycling inside the open More sheet (issue #972), wrapping from its last
   * focusable element to its first and back. The modal dialog already makes the page inert; this
   * only stops Tab leaving the sheet for the browser's own controls.
   */
  containFocus(event: KeyboardEvent): void {
    const sheet = this.sheet;
    if (event.key !== 'Tab' || sheet === undefined) {
      return;
    }

    const focusable = Array.from(sheet.querySelectorAll<HTMLElement>('a[href], button:not([disabled])'));
    if (focusable.length === 0) {
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  /** The `<nav>` panel's layout classes: the wide sidebar or rail, or the More sheet's body. */
  get panelClasses(): string {
    if (this.drawer) {
      return 'min-h-0 flex-1 w-full pb-[max(1rem,env(safe-area-inset-bottom))]';
    }
    return `shrink-0 border-r border-rr-line p-3 ${this.expanded ? 'w-64' : 'w-16'}`;
  }

  isLinkActive(link: NavLink): boolean {
    return this.activeRoute === link.route;
  }

  /** Whether the entry is one of issue #729's section headings rather than a destination. */
  isSectionHeading(item: NavItem): item is NavSectionHeading {
    return item.kind === 'section';
  }

  /**
   * Whether this destination is shown locked (issue #729). A locked entry keeps its link: clicking
   * it navigates, and `requireProvider` on the route shows the "needs a provider" page, so the
   * entry and the URL behave identically - which is exactly what the issue asks for, and is why
   * nothing here is `aria-disabled`, a claim that would be untrue of a link that does navigate.
   */
  isLinkLocked(link: NavLink): boolean {
    return navLockOf(link, this.provider) !== 'open';
  }

  /** The one sentence a locked entry carries, as its accessible description and its tooltip. */
  lockSummary(link: NavLink): string {
    return providerLockSummary(this.lockOf(link), link.label);
  }

  /**
   * Whether this destination is the switchable one and the application has switched it off
   * (issue #873), which is what the `Off` pill is rendered from.
   *
   * A member who is not the Super Admin is not offered the entry at all in that state, so a pill
   * here is by construction a Super Admin's: it tells them, where they are looking, that what they
   * can see is hidden from everybody else.
   */
  isOptionSwitchedOff(link: NavLink): boolean {
    return optionSwitchedOff(link.route, this.menuInputs?.options);
  }

  /** The sentence a screen reader is given beside the `Off` pill. */
  optionOffSummary(link: NavLink): string {
    return `${link.label} is switched off and hidden from everybody but a Super Admin.`;
  }

  /**
   * A destination's tooltip: the lock sentence while it is locked, because that is what an operator
   * needs before clicking; otherwise the label the collapsed rail has no room to print.
   */
  linkTitle(link: NavLink): string | null {
    const summary = this.lockSummary(link);
    if (summary !== '') {
      return summary;
    }
    return this.expanded ? null : link.label;
  }

  private lockOf(link: NavLink): ProviderLock {
    return navLockOf(link, this.provider);
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

  /**
   * Rebuilds the menu from the answers read so far. It renders nothing until the access answers
   * have arrived - a menu that offered everything and then removed what the role cannot use would
   * show entries that were never this member's - and it is called again whenever either the access
   * answers or the provider answer changes.
   */
  private renderNavigation(): void {
    const inputs = this.menuInputs;
    if (inputs === undefined) {
      return;
    }

    this.navigation = navigationFor(
      inputs.capabilities,
      inputs.diagnosticsOffered,
      inputs.businessNavigation,
      this.provider,
      inputs.options
    );
    // The More sheet opens every group (issue #972): the whole permitted menu is reachable by
    // scrolling alone, which is what a narrow screen offers instead of a rail. A group can still be
    // closed from its heading.
    if (this.drawer) {
      for (const item of this.navigation) {
        if (item.kind === 'group' && !this.sheetGroupsOpened.has(item.label)) {
          this.sheetGroupsOpened.add(item.label);
          this.openGroups.add(item.label);
        }
      }
    }
    // A page opened by URL before the answers arrived becomes the active entry once its link
    // exists, so the owning group reflects where the operator actually is.
    this.applyActiveRoute(this.router.url);
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

  /**
   * Reads the count once, for the state the subscription above is in. A refusal leaves the badge
   * `null` and asks for nothing further: `GET .../pending-count` is classified `platform-admin`
   * (issue #673), so its `403` refreshes neither member cache - and a refreshed capability set is
   * exactly what used to bring this method straight back round.
   */
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
 * The switchable-option answer to render the menu from (issue #873). Only a granted read carries
 * one; `unread` and `unavailable` are both "not known", which hides nothing and shows no pill - the
 * answer travels with the capabilities, and a menu must not take an entry away over a read that
 * never answered.
 */
export function renderedOptionVisibility(state: MemberAccessState): OptionVisibility | undefined {
  return state.status === 'granted' ? state.access.optionVisibility : undefined;
}

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
export function renderedCapabilities(state: MemberAccessState): CapabilitySet | undefined {
  if (state.status === 'granted') {
    return state.access.capabilities;
  }

  return state.status === 'unread' ? NO_CAPABILITIES : undefined;
}

/**
 * Opens the sheet as a modal dialog. A host without `showModal()` (the unit-test DOM) still gets an
 * open dialog, so the sheet renders and can be tested; every browser the app supports has it, and
 * the browser run in `e2e/visual/dashboard-redesign.visual.ts` is what proves the page is inert.
 */
function openModal(dialog: HTMLDialogElement): void {
  if (typeof dialog.showModal === 'function') {
    dialog.showModal();
  } else {
    dialog.setAttribute('open', '');
  }
}

function closeModal(dialog: HTMLDialogElement): void {
  if (typeof dialog.close === 'function') {
    dialog.close();
  } else {
    dialog.removeAttribute('open');
  }
}
