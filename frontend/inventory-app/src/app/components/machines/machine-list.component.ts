import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { MachineService } from '../../services/machine.service';
import { Machine } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';
import { NayaxStatusBannerComponent } from '../shared/nayax-status-banner/nayax-status-banner.component';
import { AccessService } from '../../access/access.service';
import { ProviderAvailabilityService } from '../../provider/provider-availability.service';
import { loadProviderList } from '../../provider/provider-list-load';
import { LocalMachineListComponent } from './local-machines/local-machine-list.component';

/**
 * The Machines page.
 *
 * <p><strong>It is two lists, and only one of them needs a provider</strong> (issue #740, parent
 * #329 child G1). The business's own machine records, with the actions that maintain them, are
 * {@link LocalMachineListComponent} and are always shown: a business with no sales provider creates,
 * renames, deactivates and reactivates its machines there. The live Nayax fleet below it - the
 * revenue and profit figures, and the links into each machine's stock and Sync Restock workflow -
 * is shown only while the shared availability signal (issue #728) says the fleet read can be served.
 * That is why the destination left the provider-locked section of issue #729: the page always has
 * something to show, so it is never locked.</p>
 *
 * <p>An outage is not an unavailable provider: a transport failure or a `502` is reported as an
 * error <em>in place of</em> the fleet, through the one shared path
 * ({@link loadProviderList}), because presenting it as "this business has no machines" is a claim
 * nobody made.</p>
 */
@Component({
  selector: 'app-machine-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NayaxStatusBannerComponent, LocalMachineListComponent],
  templateUrl: './machine-list.component.html'
})
export class MachineListComponent implements OnInit, OnDestroy {
  machines: Machine[] = [];
  filteredMachines: Machine[] = [];
  search = '';
  readonly loadState = new ListLoadState();

  /**
   * Whether the live Nayax fleet part of the page is shown at all (issue #740). It is not when the
   * provider cannot serve the fleet read: the local machine list is then the whole page, rather
   * than a screen of refusals beside it.
   */
  fleetShown = true;

  /** Set when the fleet could not be read for a reason that is not an availability answer. */
  fleetError = '';

  /**
   * Whether the signed-in member's role grants `Dashboard.ViewFinancials` (issue #699): the
   * sales, profit and profitability-status figures on each card are hidden without it, on the
   * frontend-level check this page applies ahead of `GET /api/machines` redacting the figures
   * itself (#697/#698). Starts `true`, the fail-open default every other capability-gated
   * control in this codebase uses while the capability has not been read yet.
   */
  canViewFinancials = true;

  private readonly financialsAccessSubscription: Subscription;

  constructor(
    private readonly machineService: MachineService,
    private readonly provider: ProviderAvailabilityService,
    access: AccessService
  ) {
    this.financialsAccessSubscription = access
      .allows('Dashboard.ViewFinancials')
      .subscribe((allowed) => (this.canViewFinancials = allowed));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.financialsAccessSubscription.unsubscribe();
  }

  load(): void {
    const token = this.loadState.start();
    loadProviderList(this.provider, 'machineFleet', () => this.machineService.getAll()).subscribe((outcome) => {
      if (this.loadState.isCurrent(token)) {
        this.machines = outcome.kind === 'listed' ? outcome.items : [];
        this.fleetShown = outcome.kind !== 'unavailable';
        this.fleetError =
          outcome.kind === 'failed'
            ? 'The live machine list could not be loaded from Nayax. Reload the page to try again.'
            : '';
        this.applyFilter();
      }
      this.loadState.succeed(token);
    });
  }

  onSearchChange(value: string): void {
    this.search = value;
    this.applyFilter();
  }

  private applyFilter(): void {
    const term = this.search.trim().toLowerCase();
    this.filteredMachines = !term
      ? this.machines
      : this.machines.filter((m) =>
        (m.machineName ?? '').toLowerCase().includes(term) ||
        (m.machineNumber ?? '').toLowerCase().includes(term)
      );
  }
}
