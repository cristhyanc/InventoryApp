import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MachineService } from '../../services/machine.service';
import { Machine } from '../../models/models';
import { ListLoadState } from '../shared/list-load-state';

@Component({
  selector: 'app-machine-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './machine-list.component.html'
})
export class MachineListComponent implements OnInit {
  machines: Machine[] = [];
  filteredMachines: Machine[] = [];
  search = '';
  readonly loadState = new ListLoadState();

  constructor(private readonly machineService: MachineService) {}

  ngOnInit(): void {
    this.load();
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
