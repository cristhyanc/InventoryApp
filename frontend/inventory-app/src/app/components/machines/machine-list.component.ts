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

@Component({
  selector: 'app-machine-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NayaxStatusBannerComponent],
  templateUrl: './machine-list.component.html'
})
export class MachineListComponent implements OnInit, OnDestroy {
  machines: Machine[] = [];
  filteredMachines: Machine[] = [];
  search = '';
  readonly loadState = new ListLoadState();

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
    this.machineService.getAll().subscribe({
      next: (machines) => {
        if (this.loadState.isCurrent(token)) {
          this.machines = machines;
          this.applyFilter();
        }
        this.loadState.succeed(token);
      },
      error: () => {
        this.loadState.fail(token);
      }
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
