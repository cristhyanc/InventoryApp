import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subject, catchError, combineLatest, distinctUntilChanged, of, startWith, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../../access/access.service';
import { Capability } from '../../access/capabilities';
import { OptionVisibility } from '../../access/option-visibility';
import { NavLink, adminNavGroup, optionSwitchedOff, switchableOptionRoute } from '../../layout/navigation';
import { RolePreviewService } from '../../role-preview/role-preview.service';
import { PlatformDiagnosticsAccessService } from '../../services/platform-diagnostics-access.service';
import { HistoricalGstVisibilityToggleComponent } from './historical-gst-classification/historical-gst-visibility-toggle.component';

interface AdminCard {
  readonly route: string;
  readonly title: string;
  readonly description: string;
}

const ADMIN_CARDS: readonly AdminCard[] = [
  {
    route: '/admin/nayax-settings',
    // "Integrations" since issue #729, the same rename the sidebar's Admin group made: one
    // destination has one name, and this is where a business connects its sales provider.
    title: 'Integrations',
    description: "Connect this business's Nayax account, and configure its processing-fee rate."
  },
  {
    route: '/admin/site-commission-agreements',
    title: 'Site Commission Agreements',
    description: 'Site commission agreement form and current agreements.'
  },
  {
    route: '/admin/imports',
    title: 'Imports',
    description:
      'Nayax sales file import and template, product catalogue import and pending reimbursement XML import.'
  },
  {
    route: '/admin/historical-cost-recovery',
    title: 'Nayax Historical Cost Recovery',
    description:
      'Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.'
  },
  {
    route: '/admin/avco-transition',
    title: 'Inventory AVCO Transition Baseline',
    description:
      'Start reliable perpetual AVCO from a controlled cutover without changing incomplete legacy movements or historical Nayax-costed sales.'
  },
  {
    route: '/admin/costing-repair',
    title: 'Costing Repair',
    description:
      'A human-entered historical costing repair for a product whose cost history has a fatal missing-opening or unknown-cost issue.'
  },
  {
    route: '/admin/historical-gst-classification',
    title: 'Historical GST Classification',
    description:
      'Classify purchase components that are still not classified from the GST rules configured on your products and suppliers, after previewing exactly what would change.'
  },
  {
    route: '/admin/nayax-sale-timestamp-repair',
    title: 'Nayax Sale Timestamp Repair',
    description:
      'Repair stored Nayax sale instants that were never the authoritative ones, from verified source evidence and one transaction at a time, after previewing every row that would move.'
  },
  {
    route: '/admin/users',
    title: 'Users',
    description: "Manage your business's members and invite links."
  },
  {
    route: '/admin/roles',
    title: 'Roles',
    description: 'What Owner, Manager and Operator can each access.'
  },
  {
    route: '/admin/business-settings',
    title: 'Business Settings',
    description: "Your business's own name, contact email, ABN, GST registration and time zone."
  }
];

/**
 * Each card's route paired with the capability its own page needs, read from `adminNavGroup`
 * (`layout/navigation.ts`) rather than a second, hand-kept table: the sidebar's `Admin` group and
 * this hub must never disagree about which capability a destination needs.
 */
const ADMIN_CARD_CAPABILITY = new Map<string, Capability>(
  adminNavGroup.children
    .filter((child): child is NavLink => child.kind === 'link' && child.capability !== undefined)
    .map((child) => [child.route, child.capability as Capability])
);

/**
 * The Admin landing page. Every Admin workflow now lives on its own routed page (issues #388,
 * #389, #390 and #433), so this component holds no workflow state, no service dependency and no
 * second copy of any tool - it is the link hub the Dashboard's "Open Admin" action and each
 * dedicated page's "Back to Admin" link point at. The sidebar's Admin group (#391) is now the
 * primary way into those pages, so this is a second, still valid entry point; it therefore keeps
 * its own `/admin` address rather than redirecting.
 *
 * **It renders only the cards the member's capabilities allow (issue #674).** Before this it
 * showed every card unconditionally, including the Owner-only repair and integration tools, to a
 * Manager who can only reach `/admin` through `Imports.Run` or `SiteCommissions.Manage`; it also
 * left out Users, Roles and Business Settings entirely, even though the sidebar already offered
 * them. It decides nothing itself: `AccessService.access$` is the one cached answer every
 * capability-aware surface renders from, and `ADMIN_CARD_CAPABILITY` is read from the sidebar's own
 * `adminNavGroup` data, so a card hides from the hub for exactly the same reason, and under exactly
 * the same conditions, its sidebar link does. A hidden card hides a page rather than protects one -
 * the endpoint behind each page checks the same capability itself - and this component fetches no
 * protected data of its own to decide that; it only reads the already-cached capability set.
 *
 * **It also applies the one switchable option, and offers the Super Admin the switch** (issue #873).
 * `Historical GST Classification` is withheld here, exactly as it is withheld from the sidebar, while
 * the shared setting says it is switched off and the viewer is not the platform administrator; the
 * administrator keeps the card in both states, marked with an `Off` pill while it is off, and is
 * offered the switch itself beside it. The three answers that decide it are each somebody else's:
 * the setting travels with the capability set on `GET /api/me/access`, platform-admin access is the
 * diagnostics probe's separate answer, and a role preview withholds the administrator's own
 * treatment the same way it withholds the `Super Admin` menu group (issue #721) - because what a
 * preview shows is what the previewed role would see.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, RouterLink, HistoricalGstVisibilityToggleComponent],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Admin</h1>
          <p class="page-subtitle">Imports and maintenance tools. These actions can change application data.</p>
        </div>
      </header>

      <div class="grid gap-5 md:grid-cols-2">
        <section class="card" *ngFor="let card of cards">
          <div class="card-body">
            <h2 class="card-title">
              {{ card.title }}
              <!--
                The switched-off option (issue #873): the pill reads exactly "Off", immediately after
                the option's name, with the sentence a screen reader is given beside it. Only a Super
                Admin is shown this card in that state, so the pill is only ever rendered for them.
              -->
              @if (isSwitchedOff(card.route)) {
                <span class="badge badge-neutral">Off</span>
                <span class="sr-only">{{ card.title }} is switched off and hidden from everybody but a Super Admin.</span>
              }
            </h2>
            <p class="mt-1 text-sm value-muted">{{ card.description }}</p>
            <a [routerLink]="card.route" class="btn btn-primary mt-4 inline-flex">Open {{ card.title }}</a>
            @if (isSwitchable(card.route) && isSuperAdmin) {
              <app-historical-gst-visibility-toggle
                [visible]="!isSwitchedOff(card.route)"
                (saved)="onVisibilitySaved()"
              />
            }
          </div>
        </section>
      </div>
    </div>
  `
})
export class AdminComponent implements OnInit, OnDestroy {
  /** Starts empty: no card is offered before the member's capabilities are known. */
  cards: readonly AdminCard[] = [];

  /**
   * Whether the viewer is the platform administrator, acting as themselves. `false` until the
   * diagnostics probe confirms it, and `false` again while a role preview is running - the switch
   * belongs to the administrator, and a preview is there to show what a business role sees.
   */
  isSuperAdmin = false;

  /** The shared switchable-option answer, or `undefined` while it is not known. */
  private options?: OptionVisibility;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly access: AccessService,
    private readonly diagnosticsAccess: PlatformDiagnosticsAccessService,
    private readonly rolePreview: RolePreviewService
  ) {}

  ngOnInit(): void {
    // The probe and the preview answer different questions on their own schedules, and neither holds
    // the cards back: both start as "no", which is what every ordinary member is, and a failed probe
    // stays there silently - a link hub is not the place to report that an access check failed.
    const hasDiagnosticsAccess = this.diagnosticsAccess.granted$.pipe(
      catchError(() => of(false)),
      startWith(false)
    );

    combineLatest([
      this.access.access$,
      hasDiagnosticsAccess,
      this.rolePreview.active$.pipe(startWith(false), distinctUntilChanged())
    ])
      .pipe(takeUntil(this.destroying$))
      .subscribe(([state, diagnosticsGranted, rolePreviewActive]) => {
        this.isSuperAdmin = diagnosticsGranted && !rolePreviewActive;
        this.options = state.status === 'granted' ? state.access.optionVisibility : undefined;
        this.cards = ADMIN_CARDS.filter(
          (card) => this.isAllowed(state, card.route) && !this.isWithheld(card.route)
        );
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /** Whether this card is the one a Super Admin may switch off (issue #873). */
  isSwitchable(route: string): boolean {
    return route === switchableOptionRoute;
  }

  /** Whether the application has switched this card's option off, whoever is looking. */
  isSwitchedOff(route: string): boolean {
    return optionSwitchedOff(route, this.options);
  }

  /**
   * A saved switch is the server's new answer, so the shared read is taken again: that re-renders
   * this hub, the sidebar and everything else built from it from one answer rather than from a value
   * this page decided to believe.
   */
  onVisibilitySaved(): void {
    this.access.refresh();
  }

  /** Switched off, and the viewer is not the Super Admin the switch exempts. */
  private isWithheld(route: string): boolean {
    return this.isSwitchedOff(route) && !this.isSuperAdmin;
  }

  private isAllowed(state: MemberAccessState, route: string): boolean {
    const capability = ADMIN_CARD_CAPABILITY.get(route);
    if (capability === undefined) {
      return true;
    }

    switch (state.status) {
      case 'unread':
        return false;
      case 'unavailable':
        return true;
      default:
        return state.access.capabilities.has(capability);
    }
  }
}
