import { NgTemplateOutlet } from '@angular/common';
import { Component, ElementRef, ViewChild } from '@angular/core';
import { IconComponent } from '../../components/shared/icon.component';
import {
  LANDING_ONLINE_PRICE_NOTE,
  LANDING_SUPPLIER_COMPARISON_CATEGORY,
  LANDING_SUPPLIER_COMPARISON_HEADING,
  LANDING_SUPPLIER_COMPARISON_PRODUCT,
  LANDING_SUPPLIER_CONFIRM_NOTICE,
  LANDING_SUPPLIER_EXAMPLE_NOTICE,
  LANDING_SUPPLIER_OFFERS,
  LandingSupplierOffer,
  isLandingLowestUnitPrice,
  landingLowestUnitPriceIncGst,
  landingMoney,
  landingSupplierOfferBadges
} from './landing-content';

/** What counts as a tab stop inside the dialog: its two Close controls and nothing else - the example carries no supplier links. */
const DIALOG_TAB_STOPS = 'button:not([disabled])';

/**
 * The landing page's supplier comparison example (issue #1192, replacing issue #954's fleet-metrics
 * dashboard preview): one fictional product's offers from the supported suppliers shown in the
 * owner's design, read from nothing but `landing-content.ts`.
 *
 * From `md` up the comparison is shown inline. Below `md` a short preview stands in for it, with a
 * **View full comparison** button that opens the same table in a native `<dialog>` - a real modal,
 * opened with `showModal()`, closed by Escape, the backdrop or either Close control, and which
 * returns focus to the button that opened it. The dialog element is always in the DOM (it is never
 * created or destroyed), so its event bindings are the template's own and need no manual
 * attach/detach. No supplier name here is a link: the example makes no navigation to a supplier's
 * site.
 */
@Component({
  selector: 'app-landing-supplier-comparison-example',
  standalone: true,
  imports: [IconComponent, NgTemplateOutlet],
  templateUrl: './landing-supplier-comparison-example.component.html'
})
export class LandingSupplierComparisonExampleComponent {
  readonly heading = LANDING_SUPPLIER_COMPARISON_HEADING;
  readonly exampleNotice = LANDING_SUPPLIER_EXAMPLE_NOTICE;
  readonly confirmNotice = LANDING_SUPPLIER_CONFIRM_NOTICE;
  readonly onlinePriceNote = LANDING_ONLINE_PRICE_NOTE;
  readonly productName = LANDING_SUPPLIER_COMPARISON_PRODUCT;
  readonly categoryName = LANDING_SUPPLIER_COMPARISON_CATEGORY;
  readonly offers = LANDING_SUPPLIER_OFFERS;
  readonly dialogTitleId = 'landing-supplier-comparison-title';

  @ViewChild('dialogPanel', { static: true })
  private readonly dialogPanelRef!: ElementRef<HTMLDialogElement>;

  private trigger?: HTMLElement;

  get lowestUnitPriceIncGst(): number {
    return landingLowestUnitPriceIncGst(this.offers);
  }

  get lowestOfferCount(): number {
    return this.offers.filter((offer) => isLandingLowestUnitPrice(offer, this.offers)).length;
  }

  badgesOf(offer: LandingSupplierOffer): readonly string[] {
    return landingSupplierOfferBadges(offer, this.offers);
  }

  moneyOf(value: number): string {
    return landingMoney(value);
  }

  /** Opens the dialog, remembering the button that triggered it so closing can return focus there. */
  open(event: Event): void {
    this.trigger = event.currentTarget as HTMLElement;
    const panel = this.dialogPanelRef.nativeElement;
    panel.showModal();
    panel.focus();
  }

  close(): void {
    const panel = this.dialogPanelRef.nativeElement;
    if (!panel.open) {
      return;
    }
    // Closing the dialog before returning focus is the order that matters: while it is open the
    // rest of the page is inert, so the trigger cannot be focused yet.
    panel.close();
    const trigger = this.trigger;
    this.trigger = undefined;
    trigger?.focus();
  }

  /** The browser's own close request (Escape) leaves this component in the state Close leaves it in. */
  onCancel(event: Event): void {
    event.preventDefault();
    this.close();
  }

  /** `::backdrop` is the panel's own pseudo-element, so a click beside the panel reports the panel as its target. */
  onBackdropClick(event: MouseEvent): void {
    if (event.target === this.dialogPanelRef.nativeElement) {
      this.close();
    }
  }

  /** Escape closes; Tab and Shift+Tab cycle inside the dialog and never reach the page behind it. */
  onPanelKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
      return;
    }
    if (event.key !== 'Tab') {
      return;
    }

    event.preventDefault();
    const panel = this.dialogPanelRef.nativeElement;
    const stops = Array.from(panel.querySelectorAll<HTMLElement>(DIALOG_TAB_STOPS));
    if (stops.length === 0) {
      panel.focus();
      return;
    }
    const current = stops.indexOf(document.activeElement as HTMLElement);
    const from = current === -1 && event.shiftKey ? 0 : current;
    const step = event.shiftKey ? -1 : 1;
    stops[(from + step + stops.length) % stops.length].focus();
  }
}
