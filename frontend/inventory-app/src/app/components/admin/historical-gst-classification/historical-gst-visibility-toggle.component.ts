import { Component, EventEmitter, Input, OnDestroy, Output } from '@angular/core';
import { Subject, takeUntil } from 'rxjs';
import { OptionVisibilityService } from '../../../services/option-visibility.service';

/**
 * What this control may say about the shared setting (issue #873).
 *
 * The four answers are deliberately four, and **`unread` and `unavailable` are not `on`**: the
 * setting travels with the capability set, so before that read answers, and when it could not
 * answer, nobody has said whether the option is offered. Rendering either of those as `on` would
 * have the switch state a setting as a fact and offer to change it from a value nothing stored -
 * which is also how a successful save could be followed by the screen claiming the opposite of what
 * it stored. Navigation may fail open over an unknown answer, because an entry nobody hid is the
 * state the application was in before the setting existed; telling the administrator the setting is
 * definitely on is a different claim, and this control makes it only when it has been read.
 */
export type SwitchableOptionDisplay = 'on' | 'off' | 'unread' | 'unavailable';

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
 * one. A button carries none - what is displayed is {@link state}, the answer the server gave, and it
 * changes only when the server's own answer does. A failure therefore leaves the label, the `Off`
 * pill and the menu exactly as the stored setting has them, and says that the change was not saved.
 *
 * **An answer nobody has given yet is not a state, and is never offered as one.** While the setting
 * is `unread` or `unavailable` the switch is not rendered at all: there is nothing to switch *from*,
 * so a control offering to hide or show the option would be submitting the opposite of a value that
 * was invented here. What is rendered instead says which of the two it is, and `unavailable` offers
 * reading the setting again. {@link refreshFailed} says the same thing about a re-read that failed
 * while a known answer is on screen: that answer stands - it is the last one the server gave - and
 * the other surfaces built from the shared read may still show the previous one until it is read.
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
        {{ stateDescription }}
        Super Admins always see it and keep their existing access, in both states.
      </p>
      @if (isKnown) {
        <button
          [id]="controlId"
          type="button"
          class="btn btn-secondary mt-2 inline-flex"
          [disabled]="saving"
          [attr.aria-describedby]="descriptionId"
          (click)="apply()"
        >
          {{
            state === 'on'
              ? 'Hide Historical GST Classification from other users'
              : 'Show Historical GST Classification to other users'
          }}
        </button>
      }
      @if (showRetry) {
        <button
          [id]="retryId"
          type="button"
          class="btn btn-secondary mt-2 mr-2 inline-flex"
          [disabled]="saving || refreshing"
          [attr.aria-describedby]="descriptionId"
          (click)="retry.emit()"
        >
          Read this setting again
        </button>
      }
      @if (statusMessage) {
        <p class="mt-1 text-xs value-muted" role="status">{{ statusMessage }}</p>
      }
      @if (errorMessage) {
        <p class="mt-1 text-sm text-red-600" role="alert">{{ errorMessage }}</p>
      }
      @if (refreshFailed) {
        <p class="mt-1 text-sm text-red-600" role="alert">
          The stored setting could not be read just now, so the menus may still show its previous
          state. What is shown here is the last answer the server gave.
        </p>
      }
    </div>
  `
})
export class HistoricalGstVisibilityToggleComponent implements OnDestroy {
  /** A stable id, so the hub's own spec and an operator's tooling can name this one control. */
  readonly controlId = 'historical-gst-classification-visibility';

  readonly retryId = 'historical-gst-classification-visibility-retry';

  readonly descriptionId = 'historical-gst-classification-visibility-state';

  /**
   * The shared setting as the server last answered it, or which of the two "nobody has said" answers
   * applies. It is the only thing this control displays: there is no local copy of the setting to
   * drift from it, and no conversion of an unknown answer into a known one.
   */
  @Input() state: SwitchableOptionDisplay = 'unread';

  /** True while the hub's re-read of the shared answer has not answered yet. */
  @Input() refreshing = false;

  /** True when that re-read could not answer, so what is on screen is the last stored answer. */
  @Input() refreshFailed = false;

  /** The state a save stored, reported once the server has confirmed it and never before. */
  @Output() readonly saved = new EventEmitter<boolean>();

  /** Asks the hub to read the shared answer again; it owns that read, this control does not. */
  @Output() readonly retry = new EventEmitter<void>();

  saving = false;

  errorMessage: string | null = null;

  private readonly destroying$ = new Subject<void>();

  constructor(private readonly service: OptionVisibilityService) {}

  /** Whether the server's own answer is known, which is the only state that can be switched. */
  get isKnown(): boolean {
    return this.state === 'on' || this.state === 'off';
  }

  /** Reading the setting again is offered whenever the last read of it did not answer. */
  get showRetry(): boolean {
    return this.state === 'unavailable' || this.refreshFailed;
  }

  /** What the setting is, in the sentence the control is described by. */
  get stateDescription(): string {
    switch (this.state) {
      case 'on':
        return 'This tool is shown to every user whose role allows it.';
      case 'off':
        return 'This tool is hidden from everybody but a Super Admin.';
      case 'unread':
        return 'Whether this tool is shown to other users is still being read.';
      default:
        return 'Whether this tool is shown to other users could not be read, so it cannot be changed here until it has been.';
    }
  }

  /** The one live-region sentence: a save in flight, or a read of the setting in flight. */
  get statusMessage(): string | null {
    if (this.saving) {
      return 'Saving…';
    }
    if (this.refreshing || this.state === 'unread') {
      return 'Reading the stored setting…';
    }
    return null;
  }

  /** Switches the option to the other state, which is the one thing this control does. */
  apply(): void {
    if (this.saving || !this.isKnown) {
      return;
    }

    // The opposite of the server's own answer, never of a value this control assumed: that is what
    // makes the next click after a stored `off` ask for `on` rather than repeat `off`.
    const requested = this.state === 'off';
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
