import { Component, ElementRef, Input, NgZone, OnDestroy, Renderer2, ViewChild } from '@angular/core';
import { IconComponent } from '../icon.component';

/** How wide the panel is when the viewport has room for it, and the margin it keeps from the edges. */
const PANEL_WIDTH = 288;
const VIEWPORT_MARGIN = 8;

/** The gap between the button and the panel, so the panel sits beside it rather than on it. */
const TRIGGER_GAP = 8;

/** Keeps a value inside a range, and never returns a smaller lower bound than it was given. */
function clamp(value: number, lowest: number, highest: number): number {
  return Math.max(lowest, Math.min(value, Math.max(lowest, highest)));
}

/**
 * One icon-only help button and the short explanation it opens beside itself - the shared help
 * toggletip of issues #1015 and #1016, built once on the owner's decision of 31 October 2026 so the
 * two places that need it cannot drift apart or duplicate the interaction.
 *
 * **It holds no text of its own.** The sentence is projected by the caller, which keeps each
 * issue's own wording in its own template, and the button's accessible name is the caller's `label`
 * ("About Cheapest supplier"), so a page full of these says which column each one explains.
 *
 * **The interaction both issues ask for, in one place.** The button carries `aria-expanded` and
 * `aria-controls`, and describes itself with the open panel, so the explanation is reachable by
 * keyboard and by assistive technology rather than only by a native `title` tooltip. Click and tap
 * toggle it; Enter and Space are answered here and their default activation prevented, so one key
 * press is one toggle rather than two; hover and keyboard focus open it transiently and let it go
 * again; Escape and a click outside close it, and Escape returns focus to the button. Only one
 * panel is open in the application at a time, so one mobile card never explains another.
 *
 * **The panel is in the top layer, placed from the button.** It is rendered with the HTML `popover`
 * attribute and shown with `showPopover()`, which is what keeps the `overflow-hidden` card and the
 * horizontally scrolling table around the Reorder priorities columns from clipping it; `place()`
 * then puts it under the button (or above it, when there is no room below), centred on it and
 * clamped inside the viewport, so it stays readable at 360px and the table never moves. A browser
 * without the Popover API still renders and places the panel - it is simply not in the top layer.
 */
@Component({
  selector: 'app-help-toggletip',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './help-toggletip.component.html'
})
export class HelpToggletipComponent implements OnDestroy {
  private static instances = 0;

  /**
   * The one open panel, if any. A toggletip is opened from a pointer or a key press and closed by
   * the next one, so "which of them is open" is a fact about the application rather than about any
   * one instance; keeping it here is what makes opening one close the others.
   */
  private static openToggletip?: HelpToggletipComponent;

  /** The button's accessible name, which has to name what it explains. */
  @Input({ required: true }) label = '';

  /**
   * This instance's own panel id, so a column of these produces no duplicate id and every button
   * controls and describes itself rather than the first panel on the page.
   */
  readonly panelId = `help-toggletip-${++HelpToggletipComponent.instances}`;

  open = false;

  /**
   * Whether it was opened by a click, a tap or a key rather than by hover or focus alone. A pinned
   * panel stays open when the pointer or focus leaves, because somebody asked for it on purpose.
   */
  private pinned = false;

  /**
   * Set while focus is being put back on the button by a close, so the `focus` event that restores
   * it is not read as somebody focusing the button and does not open the panel straight back up.
   */
  private returningFocus = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;

  /** The listeners bound for one opening, undone when it closes with the element they belong to. */
  private readonly openListeners: (() => void)[] = [];

  constructor(
    private readonly host: ElementRef<HTMLElement>,
    private readonly renderer: Renderer2,
    private readonly zone: NgZone
  ) {}

  @ViewChild('panel')
  private set panelElement(panel: ElementRef<HTMLElement> | undefined) {
    if (panel !== undefined) {
      this.show(panel.nativeElement);
    }
  }

  ngOnDestroy(): void {
    this.close();
  }

  /** Click, tap, Enter and Space: open and pin it, or close an already pinned panel. */
  toggle(): void {
    if (this.open && this.pinned) {
      this.closeAndReturnFocus();
      return;
    }

    this.openPanel();
    this.pinned = true;
  }

  /**
   * Enter and Space are handled here and their default prevented, because a button's own
   * activation would otherwise click it as well and toggle the panel straight back.
   */
  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
      event.preventDefault();
      this.toggle();
    }
  }

  onPointerEnter(): void {
    this.openPanel();
  }

  onPointerLeave(): void {
    if (!this.pinned) {
      this.close();
    }
  }

  onFocus(): void {
    if (!this.returningFocus) {
      this.openPanel();
    }
  }

  /** Focus leaving the button and the panel closes a panel nobody pinned. */
  onFocusOut(event: FocusEvent): void {
    const movedTo = event.relatedTarget;
    if (this.pinned || (movedTo instanceof Node && this.host.nativeElement.contains(movedTo))) {
      return;
    }

    this.close();
  }

  private openPanel(): void {
    if (this.open) {
      return;
    }

    HelpToggletipComponent.openToggletip?.close();
    HelpToggletipComponent.openToggletip = this;
    this.open = true;
  }

  private close(): void {
    if (!this.open) {
      return;
    }

    this.open = false;
    this.pinned = false;
    if (HelpToggletipComponent.openToggletip === this) {
      HelpToggletipComponent.openToggletip = undefined;
    }

    for (const unlisten of this.openListeners) {
      unlisten();
    }
    this.openListeners.length = 0;
  }

  /**
   * Closes it and puts focus back on the button, which is where somebody who opened it with the
   * keyboard is. A panel that was only hovered, with focus somewhere else entirely, is closed
   * without moving focus: taking it would interrupt whatever the person is actually typing in.
   */
  private closeAndReturnFocus(): void {
    const hadFocus = this.pinned || this.host.nativeElement.contains(document.activeElement);
    this.close();

    if (hadFocus) {
      this.returningFocus = true;
      this.trigger?.nativeElement.focus();
      this.returningFocus = false;
    }
  }

  /**
   * Hands the panel to the browser's top layer and places it, then binds the behaviour that is
   * about the event rather than about an element: which click counts as outside, Escape from
   * wherever focus is, and keeping the panel beside its button while the page scrolls or resizes.
   */
  private show(panel: HTMLElement): void {
    // A browser without the Popover API leaves the panel in the page, positioned the same way; it
    // is only the top layer, and so the freedom from an ancestor's `overflow`, that is lost.
    if (typeof panel.showPopover === 'function') {
      panel.showPopover();
    }

    this.place(panel);

    this.openListeners.push(
      this.renderer.listen('document', 'click', (event: MouseEvent) => {
        if (event.target instanceof Node && !this.host.nativeElement.contains(event.target)) {
          this.close();
        }
      }),
      this.renderer.listen('document', 'keydown', (event: KeyboardEvent) => {
        if (event.key === 'Escape') {
          event.preventDefault();
          this.closeAndReturnFocus();
        }
      })
    );

    // Repositioning changes nothing Angular renders, so it runs outside the zone rather than
    // starting a change-detection pass for every frame of a scroll. `capture` is what makes a
    // scroll inside the table wrapper - which does not bubble - reach this at all.
    this.zone.runOutsideAngular(() => {
      const reposition = (): void => this.place(panel);
      window.addEventListener('resize', reposition);
      document.addEventListener('scroll', reposition, true);
      this.openListeners.push(() => {
        window.removeEventListener('resize', reposition);
        document.removeEventListener('scroll', reposition, true);
      });
    });
  }

  /**
   * Puts the panel under its button and inside the viewport: centred on the button, clamped to the
   * viewport's margins so a button near the right edge of a 360px screen cannot push it off, and
   * flipped above the button when there is not enough room below it. `right`, `bottom` and the
   * margin are cleared because the `popover` attribute's own browser styles centre it otherwise.
   */
  private place(panel: HTMLElement): void {
    const button = this.trigger?.nativeElement.getBoundingClientRect();
    if (button === undefined) {
      return;
    }

    const width = Math.min(PANEL_WIDTH, window.innerWidth - 2 * VIEWPORT_MARGIN);
    for (const [property, value] of [
      ['right', 'auto'],
      ['bottom', 'auto'],
      ['margin', '0'],
      ['width', `${width}px`]
    ]) {
      this.renderer.setStyle(panel, property, value);
    }

    const left = clamp(
      button.left + button.width / 2 - width / 2,
      VIEWPORT_MARGIN,
      window.innerWidth - width - VIEWPORT_MARGIN
    );
    const below = button.bottom + TRIGGER_GAP;
    const fitsBelow = below + panel.offsetHeight + VIEWPORT_MARGIN <= window.innerHeight;
    const top = fitsBelow ? below : Math.max(VIEWPORT_MARGIN, button.top - TRIGGER_GAP - panel.offsetHeight);

    this.renderer.setStyle(panel, 'left', `${left}px`);
    this.renderer.setStyle(panel, 'top', `${top}px`);
  }
}
