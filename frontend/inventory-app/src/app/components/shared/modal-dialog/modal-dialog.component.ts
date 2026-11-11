import { Component, ElementRef, EventEmitter, Input, OnDestroy, Output, Renderer2, ViewChild } from '@angular/core';

/**
 * What counts as a tab stop inside a dialog. `tabindex="-1"` is excluded because the panel itself
 * carries it to be focusable without being tabbable.
 */
const DIALOG_TAB_STOPS = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])'
].join(',');

/**
 * The shared accessible dialog shell (issue #1174): a labelled, centred desktop modal that becomes
 * a full-screen sheet on a phone, with a scrollable body between a fixed header and a fixed action
 * row. Content is projected; the default slot becomes the body and anything carrying
 * `dialog-actions` becomes the footer.
 *
 * **It is a real modal.** The panel is a native `<dialog>` opened with `showModal()` as soon as it
 * renders, so the browser puts it in the top layer, makes the rest of the page inert and paints the
 * overlay as the panel's own `::backdrop`. The Tab cycle is written down here rather than left to
 * the browser, so a regression test can hold it to that, and focus returns to whatever had it when
 * the dialog opened - the control that opened it - once the dialog closes.
 *
 * **It decides nothing about closing.** Escape, the backdrop and the Close control all report
 * {@link closeRequested} and nothing else; the owner decides whether that dismisses the dialog or
 * asks a question first, which is what lets a dirty form keep itself on screen (issue #1174's
 * discard prompt). Removing the component from the template is what actually closes the panel.
 *
 * **Initial focus** is the first element carrying `data-dialog-autofocus`, or the panel itself when
 * there is none - never a control the owner did not nominate, because "sensible initial focus"
 * depends on what the dialog is for.
 */
@Component({
  selector: 'app-modal-dialog',
  standalone: true,
  templateUrl: './modal-dialog.component.html',
  styles: [
    `
      /*
       * Plain CSS, not run through the Tailwind/PostCSS pipeline, so the #410 tokens cannot be
       * referenced with @apply here. Every literal below is copied from the matching
       * tailwind.config.js token (md-gray-200/500/800, md-dialog, md-lg), exactly as
       * MachineRestockSyncComponent's dialog styles do.
       */
      .app-dialog {
        display: flex;
        flex-direction: column;
        margin: auto;
        width: calc(100% - 2rem);
        max-width: 37.5rem; /* 600px: the reviewed desktop width for this form */
        max-height: calc(100vh - 2rem);
        max-height: calc(100dvh - 2rem);
        padding: 0;
        border: 0;
        border-radius: 0.75rem;
        background: #fff;
        color: #262626;
        text-align: left;
        box-shadow:
          0 10px 15px -3px rgba(0, 0, 0, 0.1),
          0 4px 6px -2px rgba(0, 0, 0, 0.05);
        outline: none;
      }

      .app-dialog::backdrop {
        background: rgba(38, 38, 38, 0.5);
      }

      .app-dialog-header,
      .app-dialog-footer {
        flex-shrink: 0;
        display: flex;
        gap: 0.75rem;
        padding: 1rem 1.5rem;
      }

      .app-dialog-header {
        align-items: flex-start;
        justify-content: space-between;
        border-bottom: 1px solid #e5e5e5;
      }

      .app-dialog-footer {
        flex-wrap: wrap;
        justify-content: flex-end;
        border-top: 1px solid #e5e5e5;
      }

      .app-dialog-title {
        margin: 0;
        font-size: 1.125rem;
        font-weight: 600;
        color: #262626;
      }

      .app-dialog-body {
        flex: 1 1 auto;
        min-height: 0;
        overflow-y: auto;
        overscroll-behavior: contain;
        padding: 1rem 1.5rem;
      }

      /* At least 44px square, so the dismissal is a comfortable touch target on a phone. */
      .app-dialog-close {
        flex-shrink: 0;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 2.75rem;
        min-height: 2.75rem;
        border: none;
        background: transparent;
        color: #737373;
        font-size: 1.5rem;
        line-height: 1;
        border-radius: 0.375rem;
        cursor: pointer;
      }

      .app-dialog-close:hover:not(:disabled) {
        background: #f5f5f5;
      }

      .app-dialog-close:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }

      /*
       * Full screen on a phone. The dvh unit keeps the panel inside the viewport the browser's own
       * chrome leaves (with a vh fallback for a browser that has only that), the body is the one
       * thing that scrolls, and the action row keeps clear of the home indicator through
       * safe-area-inset-bottom. A virtual keyboard that overlays the viewport rather than
       * resizing it is the platform's business: the body scrolls, so the focused field can still
       * be brought above the keyboard.
       */
      @media (max-width: 640px) {
        .app-dialog {
          width: 100%;
          max-width: none;
          height: 100vh;
          height: 100dvh;
          max-height: 100vh;
          max-height: 100dvh;
          margin: 0;
          border-radius: 0;
        }

        .app-dialog-header,
        .app-dialog-body,
        .app-dialog-footer {
          padding-left: 1rem;
          padding-right: 1rem;
        }

        .app-dialog-footer {
          padding-bottom: max(1rem, env(safe-area-inset-bottom));
        }
      }
    `
  ]
})
export class ModalDialogComponent implements OnDestroy {
  private static instances = 0;

  /** The dialog's accessible name, rendered as its heading. */
  @Input({ required: true }) dialogTitle!: string;

  /** The accessible name of the header's Close control. */
  @Input() closeLabel = 'Close';

  /** Holds the Close control still, for an owner that is in the middle of a write. */
  @Input() closeDisabled = false;

  /** An optional `data-testid` on the panel, so a page's own tests can find one of several dialogs. */
  @Input() testId: string | null = null;

  /** An optional `data-testid` on the heading. */
  @Input() titleTestId: string | null = null;

  /**
   * Escape, a backdrop click or the Close control. The owner decides what happens next: nothing
   * here closes the dialog by itself.
   */
  @Output() readonly closeRequested = new EventEmitter<void>();

  /** This dialog's own heading id, so two dialogs on one page never share one. */
  readonly titleId = `app-dialog-title-${++ModalDialogComponent.instances}`;

  private panel?: HTMLDialogElement;

  /** Whatever had focus when the dialog opened, so closing can give it back. */
  private opener: HTMLElement | null = null;

  private readonly panelListeners: (() => void)[] = [];

  constructor(private readonly renderer: Renderer2) {}

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel !== undefined && panel.nativeElement !== this.panel) {
      this.openAsModal(panel.nativeElement);
    }
  }

  ngOnDestroy(): void {
    this.release();
  }

  requestClose(): void {
    this.closeRequested.emit();
  }

  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;

    const active = document.activeElement;
    this.opener = active instanceof HTMLElement && active !== document.body ? active : null;

    if (typeof panel.showModal === 'function') {
      panel.showModal();
    }

    const requested = panel.querySelector<HTMLElement>('[data-dialog-autofocus]');
    (requested ?? panel).focus();

    this.panelListeners.push(
      this.renderer.listen(panel, 'keydown', (event: KeyboardEvent) => this.onPanelKeydown(panel, event)),
      // `::backdrop` is the panel's own pseudo-element, so a click beside the panel is reported
      // with the panel as its target; a click on anything inside it never is.
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.requestClose();
        }
      }),
      // The browser's own close request is answered exactly as Escape is, and never by letting the
      // platform close a dialog whose owner may still want it open.
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.requestClose();
      })
    );
  }

  private release(): void {
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;

    const panel = this.panel;
    this.panel = undefined;
    panel?.close();

    // Only now: while the dialog is open the rest of the page is inert, so the control that opened
    // it cannot take focus back yet. A control that has since left the document is left alone.
    const opener = this.opener;
    this.opener = null;
    if (opener?.isConnected) {
      opener.focus();
    }
  }

  /** Escape asks to close; Tab and Shift+Tab cycle inside the dialog and never reach the page behind it. */
  private onPanelKeydown(panel: HTMLDialogElement, event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.requestClose();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }

    event.preventDefault();
    const stops = Array.from(panel.querySelectorAll<HTMLElement>(DIALOG_TAB_STOPS));
    if (stops.length === 0) {
      panel.focus();
      return;
    }

    const current = stops.indexOf(document.activeElement as HTMLElement);
    // Wraps at either end; with nothing focused yet, Tab lands on the first stop and Shift+Tab on the last.
    const from = current === -1 && event.shiftKey ? 0 : current;
    const step = event.shiftKey ? -1 : 1;
    stops[(from + step + stops.length) % stops.length].focus();
  }
}
