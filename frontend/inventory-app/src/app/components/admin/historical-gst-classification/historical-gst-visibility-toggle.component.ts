import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { OptionVisibilityService } from '../../../services/option-visibility.service';

/**
 * The Super Admin's switch beside the Admin hub's `Historical GST Classification` card (issue #873):
 * whether that option is offered to anybody else.
 *
 * **It is its own component, not markup grown inside the Admin hub**, because it has a lifecycle the
 * hub does not: a request in flight, a saved answer and a refused one (docs/architecture.md § Page
 * composition boundary). The hub composes it in, hands it the state the server reported, and is told
 * when a save succeeded so it can re-read that shared answer.
 *
 * **Nothing here can claim a setting that was not stored.** The control is a button rather than a
 * checkbox on purpose: a checkbox carries its own state, which the browser flips on the click, so a
 * refused save would leave the box reading as the new setting while the server still holds the old
 * one. A button carries none - what is displayed is {@link visible}, the answer
 * `GET /api/me/access` reported, and it changes only when the server's own answer does. A failure
 * therefore leaves the label, the `Off` pill and the menu exactly as the stored setting has them, and
 * says that the change was not saved.
 *
 * **It decides nothing else.** Who may switch this is the API's answer: the call behind it is the
 * platform-admin endpoint, and a caller it refuses is told here rather than having a change applied
 * on screen. The option's own page keeps authorizing itself by `Data.Repair` in both states, so this
 * switch hides a tool rather than granting or revoking one.
 */
@Component({
  selector: 'app-historical-gst-visibility-toggle',
  standalone: true,
  template: `
    <div class="mt-4 border-t border-md-gray-200 pt-3">
      <p [id]="descriptionId" class="text-sm text-md-gray-700">
        {{
          visible
            ? 'This tool is shown to every user whose role allows it.'
            : 'This tool is hidden from everybody but a Super Admin.'
        }}
        Super Admins always see it and keep their existing access, in both states.
      </p>
      <button
        [id]="controlId"
        type="button"
        class="btn btn-secondary mt-2 inline-flex"
        [disabled]="saving"
        [attr.aria-describedby]="descriptionId"
        (click)="apply()"
      >
        {{
          visible
            ? 'Hide Historical GST Classification from other users'
            : 'Show Historical GST Classification to other users'
        }}
      </button>
      @if (saving) {
        <p class="mt-1 text-xs value-muted" role="status">Saving…</p>
      }
      @if (errorMessage) {
        <p class="mt-1 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
      }
    </div>
  `
})
export class HistoricalGstVisibilityToggleComponent implements OnDestroy {
  /** A stable id, so the hub's own spec and an operator's tooling can name this one control. */
  readonly controlId = 'historical-gst-classification-visibility';

  readonly descriptionId = 'historical-gst-classification-visibility-state';

  /**
   * Whether the option is currently offered to other users, as the server reported it. It is the
   * only thing this control displays: there is no local copy of the setting to drift from it.
   */
  @Input() visible = true;

  /** The state a save stored, reported once the server has confirmed it and never before. */
  @Output() readonly saved = new EventEmitter<boolean>();

  saving = false;

  errorMessage: string | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly service: OptionVisibilityService) {}

  /** Switches the option to the other state, which is the one thing this control does. */
  apply(): void {
    if (this.saving) {
      return;
    }

    const requested = !this.visible;
    this.saving = true;
    this.errorMessage = null;

    this.service
      .setHistoricalGstClassificationVisible(requested)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (settings) => {
          this.saving = false;
          // The server's own answer, not the requested value: what is reported is what was stored.
          this.saved.emit(settings.historicalGstClassificationVisible);
        },
        error: () => {
          this.saving = false;
          this.errorMessage =
            'That change was not saved, so Historical GST Classification is unchanged. Try again.';
        }
      });
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }
}
