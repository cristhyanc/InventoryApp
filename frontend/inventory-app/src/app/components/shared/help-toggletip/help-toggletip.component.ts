import { Component, ElementRef, HostListener, Input, OnDestroy, ViewChild } from '@angular/core';
import { Subscription } from 'rxjs';
import { IconComponent } from '../icon.component';
import { PopoverCoordinator } from '../popover-coordinator.service';

/** The tip's width, and the gap it keeps from the window edge so it is never clipped. */
const PANEL_WIDTH_PX = 256;
const VIEWPORT_GUTTER_PX = 16;
const TRIGGER_GAP_PX = 6;

/**
 * A small "i" button that shows a short explanation beside a header or label (issue #1029), so
 * the explanation does not have to sit on the page as a paragraph. It is the shared help control
 * the Dashboard help buttons (issues #1015 and #1016) are meant to reuse.
 *
 * It is a toggletip rather than a hover tooltip: click, tap, Enter and Space open and close it, so
 * it works the same with a mouse, a finger or a keyboard. Escape or a click anywhere else closes it,
 * and only one popover is open at a time (see {@link PopoverCoordinator}). The text is announced
 * through a status region as it appears.
 *
 * The panel is positioned against the window rather than inside its parent, and kept a gutter away
 * from each side, so a table or a card around the button can never clip it.
 */
@Component({
  selector: 'app-help-toggletip',
  standalone: true,
  imports: [IconComponent],
  template: `
    <span class="inline-flex align-middle">
      <button
        #trigger
        type="button"
        data-testid="help-toggletip-trigger"
        class="inline-flex h-6 w-6 items-center justify-center rounded-full text-rr-caption hover:text-rr-teal focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-rr-teal"
        [attr.aria-label]="'About ' + label"
        [attr.aria-expanded]="open"
        [attr.aria-controls]="open ? panelId : null"
        (click)="toggle()"
      >
        <app-icon name="info" [size]="16" />
      </button>
      <span role="status">
        @if (open) {
          <span
            data-testid="help-toggletip-panel"
            class="fixed z-50 block rounded-lg border border-rr-line bg-white p-3 text-left text-sm font-normal normal-case leading-snug tracking-normal text-rr-navy shadow-md-lg"
            [id]="panelId"
            [style.top.px]="top"
            [style.left.px]="left"
            [style.width.px]="width"
          >{{ text }}</span>
        }
      </span>
    </span>
  `
})
export class HelpToggletipComponent implements OnDestroy {
  private static nextId = 0;

  /** What the tip explains, read as "About {label}" by a screen reader. */
  @Input({ required: true }) label = '';

  /** The explanation itself. */
  @Input({ required: true }) text = '';

  @ViewChild('trigger', { static: true }) private trigger!: ElementRef<HTMLButtonElement>;

  open = false;
  top = 0;
  left = 0;
  width = PANEL_WIDTH_PX;

  readonly panelId = `help-toggletip-${HelpToggletipComponent.nextId++}`;

  private readonly subscription: Subscription;

  constructor(
    private readonly host: ElementRef<HTMLElement>,
    private readonly coordinator: PopoverCoordinator
  ) {
    this.subscription = coordinator.opened$.subscribe((owner) => {
      if (owner !== this) this.open = false;
    });
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }

  toggle(): void {
    if (this.open) {
      this.open = false;
      return;
    }
    this.place();
    this.open = true;
    this.coordinator.announceOpen(this);
  }

  /** Escape closes the tip and hands focus back to its button, wherever focus had gone. */
  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (!this.open) return;
    this.open = false;
    this.trigger.nativeElement.focus();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.open && !this.host.nativeElement.contains(event.target as Node)) {
      this.open = false;
    }
  }

  /** The panel follows its button when the page scrolls or the window changes size. */
  @HostListener('window:resize')
  @HostListener('window:scroll')
  onViewportChange(): void {
    if (this.open) this.place();
  }

  /** Below the button, centred on it where there is room and pulled inside the window where not. */
  private place(): void {
    const rect = this.trigger.nativeElement.getBoundingClientRect();
    const viewportWidth = window.innerWidth || document.documentElement.clientWidth || PANEL_WIDTH_PX;
    this.width = Math.min(PANEL_WIDTH_PX, Math.max(0, viewportWidth - 2 * VIEWPORT_GUTTER_PX));
    const centred = rect.left + rect.width / 2 - this.width / 2;
    const maxLeft = viewportWidth - VIEWPORT_GUTTER_PX - this.width;
    this.left = Math.max(VIEWPORT_GUTTER_PX, Math.min(centred, maxLeft));
    this.top = rect.bottom + TRIGGER_GAP_PX;
  }
}
