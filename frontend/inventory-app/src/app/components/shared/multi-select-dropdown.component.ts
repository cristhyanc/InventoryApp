import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface MultiSelectOption {
  id: number;
  label: string;
}

/**
 * Compact checkbox-style multi-select dropdown filter (issue #226): a trigger button that
 * summarizes the current selection ("All products", "4 products selected", ...) and a popup panel
 * of checkboxes plus a tri-state "Select all". It only reports selection changes through
 * `selectedIdsChange` - staging, applying and any resulting data fetch are the caller's concern.
 */
@Component({
  selector: 'app-multi-select-dropdown',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './multi-select-dropdown.component.html'
})
export class MultiSelectDropdownComponent {
  private static nextInstanceId = 0;

  @Input({ required: true }) label = '';
  @Input({ required: true }) itemLabelSingular = '';
  @Input({ required: true }) itemLabelPlural = '';
  @Input() options: MultiSelectOption[] = [];
  @Input() selectedIds: number[] = [];
  @Output() readonly selectedIdsChange = new EventEmitter<number[]>();

  isOpen = false;
  readonly panelId = `multi-select-dropdown-panel-${MultiSelectDropdownComponent.nextInstanceId++}`;

  @ViewChild('trigger') private trigger?: ElementRef<HTMLButtonElement>;

  constructor(private elementRef: ElementRef<HTMLElement>) {}

  get allSelected(): boolean {
    return this.options.length > 0 && this.selectedIds.length === this.options.length;
  }

  get noneSelected(): boolean {
    return this.selectedIds.length === 0;
  }

  get someSelected(): boolean {
    return !this.allSelected && !this.noneSelected;
  }

  get triggerText(): string {
    if (this.options.length === 0) {
      return `No ${this.itemLabelPlural} available`;
    }
    if (this.noneSelected) {
      return `No ${this.itemLabelPlural} selected`;
    }
    if (this.allSelected) {
      return `All ${this.itemLabelPlural}`;
    }
    const count = this.selectedIds.length;
    return `${count} ${count === 1 ? this.itemLabelSingular : this.itemLabelPlural} selected`;
  }

  isSelected(id: number): boolean {
    return this.selectedIds.includes(id);
  }

  toggleOpen(): void {
    this.isOpen = !this.isOpen;
  }

  close(): void {
    this.isOpen = false;
  }

  toggleOption(id: number): void {
    const next = this.isSelected(id) ? this.selectedIds.filter((existing) => existing !== id) : [...this.selectedIds, id];
    this.selectedIdsChange.emit(next);
  }

  toggleSelectAll(): void {
    this.selectedIdsChange.emit(this.allSelected ? [] : this.options.map((option) => option.id));
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.isOpen && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.isOpen) {
      this.close();
      this.trigger?.nativeElement.focus();
    }
  }
}
