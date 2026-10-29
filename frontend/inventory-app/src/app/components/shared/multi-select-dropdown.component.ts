import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface MultiSelectOption {
  id: number;
  label: string;
  /**
   * An optional heading this option belongs under (issue #996): the Pick List's machine filter
   * passes each machine's site, so a site's machines are offered together. Options with no group
   * are listed last, under no heading at all, so a machine at no known site stays selectable
   * without an invented one. A filter whose options carry no group renders exactly as before.
   */
  group?: string;
}

/** One heading of the option panel and the options under it; `label` is `null` for the ungrouped ones. */
export interface MultiSelectOptionGroup {
  readonly key: string;
  readonly label: string | null;
  readonly options: readonly MultiSelectOption[];
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
  templateUrl: './multi-select-dropdown.component.html',
  styles: [
    `
      /*
       * One row structure for "Select all" and every option, so their checkboxes share a
       * fixed-width column and all label text starts at the same x-position regardless of how
       * long its label is. The global "input, select, textarea" rule in styles.scss excludes
       * checkboxes (issue #874), so .msd-checkbox's own size is a restatement of their native
       * size for this fixed-width column, not a fix for the global rule stretching them.
       */
      .msd-row {
        display: flex;
        align-items: center;
        gap: 0.5rem;
      }

      .msd-checkbox {
        flex: 0 0 auto;
        width: 1rem;
        height: 1rem;
        margin: 0;
        padding: 0;
      }

      .msd-label {
        flex: 1 1 auto;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    `
  ]
})
export class MultiSelectDropdownComponent {
  private static nextInstanceId = 0;

  @Input({ required: true }) label = '';
  @Input({ required: true }) itemLabelSingular = '';
  @Input({ required: true }) itemLabelPlural = '';
  @Input() options: MultiSelectOption[] = [];

  /**
   * What to head the options that carry no {@link MultiSelectOption.group} with, while others do.
   * Empty - the default - leaves them unheaded, and they are never headed when no option is
   * grouped at all, because a lone heading over every option says nothing.
   */
  @Input() ungroupedGroupLabel = '';
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

  /**
   * The options under their headings, in the order they arrived: one group per distinct `group`
   * label, then the options that carry none. A set of options with no group at all is one unheaded
   * group, which renders exactly the flat list this component has always rendered.
   */
  get optionGroups(): readonly MultiSelectOptionGroup[] {
    const groups: MultiSelectOptionGroup[] = [];
    const byLabel = new Map<string, MultiSelectOption[]>();
    const ungrouped: MultiSelectOption[] = [];
    for (const option of this.options) {
      if (option.group === undefined) {
        ungrouped.push(option);
        continue;
      }
      const existing = byLabel.get(option.group);
      if (existing) {
        existing.push(option);
      } else {
        byLabel.set(option.group, [option]);
      }
    }
    for (const [label, options] of byLabel) {
      groups.push({ key: `group-${label}`, label, options });
    }
    if (ungrouped.length > 0) {
      const label = groups.length > 0 && this.ungroupedGroupLabel !== '' ? this.ungroupedGroupLabel : null;
      groups.push({ key: 'ungrouped', label, options: ungrouped });
    }
    return groups;
  }

  /** Whether any option named a group, which is the only case headings are drawn for. */
  get groupHeadingsShown(): boolean {
    return this.optionGroups.some((group) => group.label !== null);
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
