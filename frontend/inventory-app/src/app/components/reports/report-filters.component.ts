import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Machine } from '../../models/models';

@Component({
  selector: 'app-report-filters',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="mb-5 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div class="flex flex-wrap items-center gap-2">
        <span class="mr-1 text-xs font-semibold uppercase tracking-wide text-slate-500">Period</span>
        @for (option of options; track option[0]) {
          <button type="button" class="rounded-md px-3 py-1.5 text-sm transition" [class.bg-blue-600]="period === option[0]" [class.text-white]="period === option[0]" [class.bg-slate-100]="period !== option[0]" [class.text-slate-700]="period !== option[0]" (click)="periodChange.emit(option[0])">{{ option[1] }}</button>
        }
        <label class="ml-auto text-sm text-slate-600">Machine
          <select class="ml-2 rounded-md border border-slate-300 px-2 py-1.5" [(ngModel)]="machineId" (ngModelChange)="machineChange.emit($event)">
            <option [ngValue]="null">All machines</option>
            @for (machine of machines; track machine.machineID) { <option [ngValue]="machine.machineID">{{ machineLabel(machine) }}</option> }
          </select>
        </label>
        <button class="rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700" (click)="apply.emit()">Apply</button>
      </div>
      @if (period === 'custom') {
        <div class="mt-3 flex flex-wrap items-center gap-3 border-t border-slate-100 pt-3">
          <label class="text-sm text-slate-600">From<input class="ml-2 rounded-md border border-slate-300 px-2 py-1.5" type="date" [(ngModel)]="from" (ngModelChange)="fromChange.emit($event)" /></label>
          <label class="text-sm text-slate-600">To<input class="ml-2 rounded-md border border-slate-300 px-2 py-1.5" type="date" [(ngModel)]="to" (ngModelChange)="toChange.emit($event)" /></label>
        </div>
      }
    </div>
  `
})
export class ReportFiltersComponent {
  @Input() from = '';
  @Input() to = '';
  @Input() machineId: number | null = null;
  @Input() machines: Machine[] = [];
  @Input() period = 'thisMonth';
  @Output() fromChange = new EventEmitter<string>();
  @Output() toChange = new EventEmitter<string>();
  @Output() machineChange = new EventEmitter<number | null>();
  @Output() periodChange = new EventEmitter<string>();
  @Output() apply = new EventEmitter<void>();
  options = [['thisMonth', 'This Month'], ['lastMonth', 'Last Month'], ['currentFy', 'Current FY'], ['previousFy', 'Previous FY'], ['custom', 'Custom']];
  machineLabel(machine: Machine): string { return machine.machineName || machine.machineNumber || `Machine ${machine.machineID}`; }
}
