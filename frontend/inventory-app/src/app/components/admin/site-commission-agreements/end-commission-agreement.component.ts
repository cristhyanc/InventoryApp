import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SiteCommissionAgreement } from '../../../services/reporting.service';
import { currentDateInTimeZone } from '../../../formatting/business-time-zone';

/**
 * The "End agreement" action for one row of the current-agreements table (issue #1254).
 *
 * <p>It is its own component rather than markup grown inside
 * `SiteCommissionAgreementsComponent` because it is a distinct workflow with its own state: an
 * open/closed step, a date being chosen, the warning that date may raise, and a confirmation. The
 * page keeps the API call and the reload, so this component decides nothing about persistence
 * (docs/architecture.md, "Frontend architecture: page composition").</p>
 *
 * <p><strong>The warning is the point of the two steps.</strong> Commission is calculated on read,
 * so an end date in the past changes what every later report says the site is owed from that date
 * on. The operator therefore sees that stated before the confirm button they press, not after.</p>
 */
@Component({
  selector: 'app-end-commission-agreement',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    @if (!open) {
      <button type="button" class="btn btn-secondary btn-sm" [disabled]="saving" (click)="start()">
        End agreement
      </button>
    } @else {
      <div class="flex flex-wrap items-end gap-2">
        <div class="field mb-0">
          <label class="field-label" [attr.for]="inputId">End date</label>
          <input
            type="date"
            [id]="inputId"
            [attr.min]="minEndDate"
            [attr.max]="maxEndDate"
            [(ngModel)]="endDate"
            [disabled]="saving"
          />
        </div>
        <button type="button" class="btn btn-primary btn-sm" [disabled]="saving || !endDate" (click)="confirm()">
          Confirm end date
        </button>
        <button type="button" class="btn btn-link btn-sm" [disabled]="saving" (click)="cancel()">Cancel</button>
      </div>
      @if (endsInThePast) {
        <p class="alert alert-warning mt-2" role="alert">
          {{ pastEndDateWarning }}
        </p>
      }
    }
  `
})
export class EndCommissionAgreementComponent {
  /** The agreement this action ends. Its start and current end date bound the date picker. */
  @Input({ required: true }) agreement!: SiteCommissionAgreement;

  /** The business's IANA time zone, or `null` while it is still unknown. */
  @Input() timeZoneId: string | null = null;

  /** Whether the page is mid-write, so the controls stay inert rather than disappearing. */
  @Input() saving = false;

  /**
   * The chosen end date, emitted only once the operator has confirmed it. Named `agreementEnded`
   * rather than `ended` because the latter is a standard DOM event name.
   */
  @Output() readonly agreementEnded = new EventEmitter<string>();

  open = false;
  endDate = '';

  /** The sentence shown for an end date that has already passed. */
  readonly pastEndDateWarning =
    'This end date has already passed. Commission is calculated when a report is read, ' +
    'so commission due for dates after it will change.';

  /** Distinct per agreement, so the label points at this row's own input. */
  get inputId(): string {
    return `end-agreement-date-${this.agreement.id ?? 'new'}`;
  }

  /** An agreement never ends before it starts. */
  get minEndDate(): string {
    return asDateValue(this.agreement.effectiveFrom);
  }

  /** An agreement that already ends can be brought forward but never extended. */
  get maxEndDate(): string | null {
    return this.agreement.effectiveTo ? asDateValue(this.agreement.effectiveTo) : null;
  }

  /**
   * Whether the chosen date has already passed in the business's own time zone - the browser's
   * local date while that zone is still unknown, and never the UTC date, which is a different
   * calendar day from late morning in a zone ahead of UTC.
   */
  get endsInThePast(): boolean {
    return this.endDate !== '' && this.endDate < today(this.timeZoneId);
  }

  start(): void {
    this.open = true;
    this.endDate = '';
  }

  cancel(): void {
    this.open = false;
    this.endDate = '';
  }

  confirm(): void {
    if (this.saving || !this.endDate) {
      return;
    }
    this.agreementEnded.emit(this.endDate);
    this.open = false;
  }
}

/** The `yyyy-MM-dd` part of an API date, which is what an `<input type="date">` value is. */
function asDateValue(value: string): string {
  return value.slice(0, 10);
}

/** Today's calendar date in the business's zone, or the browser's own while it is unknown. */
function today(timeZoneId: string | null, now: Date = new Date()): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  if (timeZoneId !== null) {
    const { year, month, day } = currentDateInTimeZone(now, timeZoneId);
    return `${year}-${pad(month)}-${pad(day)}`;
  }
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
