import { Component, ElementRef, Input, NgZone, OnDestroy, Renderer2, ViewChild } from '@angular/core';
import { IconComponent } from '../icon.component';

/** The gap between the trigger and the panel, and the panel's distance from the viewport edges. */
const TRIGGER_GAP = 4;
const VIEWPORT_MARGIN = 8;

/** What counts as an item in a `menu` panel: every `menuitem` variant the projected content uses. */
const MENU_ITEMS = '[role="menuitem"]:not([disabled]),[role="menuitemcheckbox"]:not([disabled]),[role="menuitemradio"]:not([disabled])';

/** What takes focus first in a `panel`: its first enabled control. */
const PANEL_CONTROLS = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled])';

/**
 * A button that opens a small overlay of actions or options beside it (issue #1093): the Products
 * toolbar's Columns and More actions, and each product row's overflow menu.
 *
 * - `kind="menu"` is the WAI-ARIA menu button: the panel is `role="menu"`, opening it focuses the
 *   first item, Arrow Up/Down, Home and End move between items, and choosing an item closes it.
 *   The items are the consumer's own `<button role="menuitem">`/`<a role="menuitem">` elements,
 *   projected in, so each keeps its own handler, permission check and link.
 * - `kind="panel"` is a disclosure for a small form - the Columns checkboxes - which stays open
 *   while its controls are used.
 *
 * Both close on Escape (returning focus to the trigger), on a click outside, and when focus leaves
 * them; and only one is ever open: opening one closes whichever other is. The panel is a manual
 * `popover`, so it sits in the browser's top layer and no `overflow` on the table or card around
 * the trigger can clip it, positioned from the trigger's own rectangle like the help toggletip.
 */
@Component({
  selector: 'app-action-menu',
  standalone: true,
  imports: [IconComponent],
  template: `
    <span class="relative inline-flex" (focusout)="onFocusOut($event)">
      <button
        #trigger
        type="button"
        [class]="triggerClass"
        [attr.id]="triggerId"
        [attr.aria-label]="text ? null : label"
        [attr.aria-haspopup]="kind === 'menu' ? 'menu' : null"
        [attr.aria-expanded]="open"
        [attr.aria-controls]="open ? panelId : null"
        [attr.data-testid]="testId"
        (click)="toggle()"
        (keydown)="onTriggerKeydown($event)"
      >
        @if (icon) {
          <app-icon [name]="icon" [size]="18" />
        }
        @if (text) {
          <span>{{ text }}</span>
        }
      </button>
      @if (open) {
        <div
          #panel
          popover="manual"
          [id]="panelId"
          [attr.role]="kind === 'menu' ? 'menu' : 'group'"
          [attr.aria-labelledby]="triggerId"
          [attr.data-testid]="testId ? testId + '-panel' : null"
          class="fixed z-50 m-0 flex min-w-48 flex-col gap-0.5 rounded-xl border border-rr-line bg-white p-1.5 text-left text-sm text-rr-navy shadow-md-lg"
        ><ng-content /></div>
      }
    </span>
  `
})
export class ActionMenuComponent implements OnDestroy {
  private static instances = 0;
  private static openMenu?: ActionMenuComponent;

  /** The trigger's accessible name; also its visible text when `text` is not given. */
  @Input({ required: true }) label = '';
  /** Visible trigger text, when the trigger is not an icon-only button. */
  @Input() text = '';
  /** An `app-icon` name shown on the trigger. */
  @Input() icon = '';
  @Input() kind: 'menu' | 'panel' = 'menu';
  @Input() triggerClass = 'rr-button-secondary';
  @Input() testId: string | null = null;

  private readonly instance = ++ActionMenuComponent.instances;
  readonly panelId = `action-menu-panel-${this.instance}`;
  readonly triggerId = `action-menu-trigger-${this.instance}`;

  open = false;

  @ViewChild('trigger') private readonly trigger?: ElementRef<HTMLButtonElement>;
  private panelElement?: HTMLElement;
  private readonly openListeners: (() => void)[] = [];

  constructor(
    private readonly host: ElementRef<HTMLElement>,
    private readonly renderer: Renderer2,
    private readonly zone: NgZone
  ) {}

  @ViewChild('panel')
  private set panel(panel: ElementRef<HTMLElement> | undefined) {
    if (panel !== undefined && panel.nativeElement !== this.panelElement) {
      this.show(panel.nativeElement);
    }
  }

  ngOnDestroy(): void {
    this.close(false);
  }

  toggle(): void {
    if (this.open) {
      this.close(true);
    } else {
      this.openPanel();
    }
  }

  /** Arrow Down/Up on the trigger opens the menu, as the menu-button pattern expects. */
  onTriggerKeydown(event: KeyboardEvent): void {
    if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && !this.open) {
      event.preventDefault();
      this.openPanel();
    }
  }

  onPanelKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.close(true);
      return;
    }
    if (this.kind !== 'menu') {
      return;
    }

    const items = this.items();
    if (items.length === 0) {
      return;
    }
    const current = items.indexOf(document.activeElement as HTMLElement);
    let next: number | null = null;
    switch (event.key) {
      case 'ArrowDown':
        next = current === -1 || current === items.length - 1 ? 0 : current + 1;
        break;
      case 'ArrowUp':
        next = current <= 0 ? items.length - 1 : current - 1;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = items.length - 1;
        break;
      case 'Tab':
        // Tab leaves a menu: it closes, and the browser moves on from the trigger.
        this.close(false);
        return;
    }
    if (next !== null) {
      event.preventDefault();
      items[next].focus();
    }
  }

  /** Choosing an item in a `menu` closes it; a `panel` stays open while its controls are used. */
  onPanelClick(event: MouseEvent): void {
    if (this.kind !== 'menu') {
      return;
    }
    const item = (event.target as Element | null)?.closest('[role="menuitem"]');
    if (item) {
      this.close(true);
    }
  }

  onFocusOut(event: FocusEvent): void {
    const movedTo = event.relatedTarget;
    if (!this.open || !(movedTo instanceof Node) || this.host.nativeElement.contains(movedTo)) {
      return;
    }
    this.close(false);
  }

  private openPanel(): void {
    if (ActionMenuComponent.openMenu && ActionMenuComponent.openMenu !== this) {
      ActionMenuComponent.openMenu.close(false);
    }
    ActionMenuComponent.openMenu = this;
    this.open = true;
  }

  /** Closes the panel; `returnFocus` puts focus back on the trigger (Escape, a chosen item). */
  close(returnFocus: boolean): void {
    if (!this.open) {
      return;
    }
    this.open = false;
    this.panelElement = undefined;
    if (ActionMenuComponent.openMenu === this) {
      ActionMenuComponent.openMenu = undefined;
    }
    for (const unlisten of this.openListeners) {
      unlisten();
    }
    this.openListeners.length = 0;
    if (returnFocus) {
      this.trigger?.nativeElement.focus();
    }
  }

  private items(): HTMLElement[] {
    return this.panelElement ? Array.from(this.panelElement.querySelectorAll<HTMLElement>(MENU_ITEMS)) : [];
  }

  private show(panel: HTMLElement): void {
    this.panelElement = panel;
    if (typeof panel.showPopover === 'function') {
      panel.showPopover();
    }
    this.place(panel);

    const first = panel.querySelector<HTMLElement>(this.kind === 'menu' ? MENU_ITEMS : PANEL_CONTROLS);
    first?.focus();

    // The panel's own keyboard and item handling is bound here rather than in the template: the
    // panel is a container, not a control, and each handler decides about the event's target.
    this.openListeners.push(
      this.renderer.listen(panel, 'keydown', (event: KeyboardEvent) => this.onPanelKeydown(event)),
      this.renderer.listen(panel, 'click', (event: MouseEvent) => this.onPanelClick(event)),
      this.renderer.listen('document', 'click', (event: MouseEvent) => {
        if (event.target instanceof Node && !this.host.nativeElement.contains(event.target)) {
          this.close(false);
        }
      })
    );

    // Repositioning changes nothing Angular renders, so it runs outside the zone. `capture` is what
    // makes a scroll inside the table wrapper - which does not bubble - reach this at all.
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

  /** Right-aligned under the trigger, flipped above it when there is no room below. */
  private place(panel: HTMLElement): void {
    const button = this.trigger?.nativeElement.getBoundingClientRect();
    if (button === undefined) {
      return;
    }
    const width = panel.offsetWidth;
    const left = Math.max(VIEWPORT_MARGIN, Math.min(button.right - width, window.innerWidth - width - VIEWPORT_MARGIN));
    const below = button.bottom + TRIGGER_GAP;
    const fitsBelow = below + panel.offsetHeight + VIEWPORT_MARGIN <= window.innerHeight;
    const top = fitsBelow ? below : Math.max(VIEWPORT_MARGIN, button.top - TRIGGER_GAP - panel.offsetHeight);
    this.renderer.setStyle(panel, 'inset', 'auto');
    this.renderer.setStyle(panel, 'left', `${left}px`);
    this.renderer.setStyle(panel, 'top', `${top}px`);
  }
}
