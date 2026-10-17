import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Machine } from '../../models/models';

@Component({
  selector: 'app-report-filters',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="card">
      <div class="card-body flex flex-wrap items-center gap-2">
        <span class="mr-1 text-md-table-head font-bold uppercase tracking-wide text-md-gray-500">Period</span>
        @for (option of options; track option[0]) {
          <button type="button" class="btn btn-sm" [class.btn-primary]="period === option[0]" [class.btn-secondary]="period !== option[0]" (click)="periodChange.emit(option[0])">{{ option[1] }}</button>
        }
        @if (machinesError) {
          <p data-testid="report-machine-filter-error" class="ml-auto mr-2 text-md-body text-md-danger-text">{{ machinesError }}</p>
        } @else if (machines.length > 0) {
          <label class="ml-auto mr-2 text-md-body text-md-gray-600">Machine
            <select class="ml-2 inline-block w-auto" [(ngModel)]="machineId" (ngModelChange)="machineChange.emit($event)">
              <option [ngValue]="null">All machines</option>
              @for (machine of machines; track machine.machineID) { <option [ngValue]="machine.machineID">{{ machineLabel(machine) }}</option> }
            </select>
          </label>
        }
        <button type="button" class="btn btn-primary ml-auto" (click)="apply.emit()">Apply</button>
      </div>
      @if (period === 'custom') {
        <div class="card-footer flex flex-wrap items-center gap-3">
          <label class="text-md-body text-md-gray-600">From<input class="ml-2 inline-block w-auto" type="date" [(ngModel)]="from" (ngModelChange)="fromChange.emit($event)" /></label>
          <label class="text-md-body text-md-gray-600">To<input class="ml-2 inline-block w-auto" type="date" [(ngModel)]="to" (ngModelChange)="toChange.emit($event)" /></label>
        </div>
      }
    </div>
  `
})
export class ReportFiltersComponent {
  @Input() from = '';
  @Input() to = '';
  @Input() machineId: number | null = null;
  /**
   * The machines this report may be narrowed to. An **empty** list means there is no machine filter
   * at all (issue #728): either the provider cannot list machines for this business, or it has
   * none. "All machines" over nothing is not a filter, and rendering it would suggest the report is
   * narrowed when it is not.
   */
  @Input() machines: Machine[] = [];

  /**
   * Set by the page when the machine list could not be read for a reason that is **not** an
   * availability answer - a transport failure, an outage, a refusal about the caller's own access.
   * It is shown in place of the filter, because "no machines" would be a claim about the business
   * that this request never established (issue #728).
   */
  @Input() machinesError = '';

  @Input() period = 'thisMonth';
  @Output() readonly fromChange = new EventEmitter<string>();
  @Output() readonly toChange = new EventEmitter<string>();
  @Output() readonly machineChange = new EventEmitter<number | null>();
  @Output() readonly periodChange = new EventEmitter<string>();
  @Output() readonly apply = new EventEmitter<void>();
  options = [['thisMonth', 'This Month'], ['lastMonth', 'Last Month'], ['currentFy', 'Current FY'], ['previousFy', 'Previous FY'], ['custom', 'Custom']];
  machineLabel(machine: Machine): string { return machine.machineName || machine.machineNumber || `Machine ${machine.machineID}`; }
}
