import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subject, takeUntil } from 'rxjs';
import { AccessService, MemberAccessState } from '../../access/access.service';
import { Capability } from '../../access/capabilities';
import { NavLink, adminNavGroup } from '../../layout/navigation';

interface AdminCard {
  readonly route: string;
  readonly title: string;
  readonly description: string;
}

const ADMIN_CARDS: readonly AdminCard[] = [
  {
    route: '/admin/nayax-settings',
    title: 'Nayax Settings',
    description: 'Processing-fee rate configuration and configured-rate history.'
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
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, RouterLink],
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
            <h2 class="card-title">{{ card.title }}</h2>
            <p class="mt-1 text-sm value-muted">{{ card.description }}</p>
            <a [routerLink]="card.route" class="btn btn-primary mt-4 inline-flex">Open {{ card.title }}</a>
          </div>
        </section>
      </div>
    </div>
  `
})
export class AdminComponent implements OnInit, OnDestroy {
  /** Starts empty: no card is offered before the member's capabilities are known. */
  cards: readonly AdminCard[] = [];

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly access: AccessService) {}

  ngOnInit(): void {
    this.access.access$.pipe(takeUntil(this.destroying$)).subscribe((state) => {
      this.cards = ADMIN_CARDS.filter((card) => this.isAllowed(state, card.route));
    });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
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
