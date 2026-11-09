import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { LocalMachine, Product, StockAdjustment, StockAdjustmentReason } from '../../../models/models';
import { StockService } from '../../../services/stock.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { StockAdjustmentPanelComponent } from './stock-adjustment-panel.component';

const dialogPrototype = Object.getPrototypeOf(document.createElement('dialog')) as HTMLDialogElement;

function product(overrides: Partial<Product> = {}): Product {
  return { id: 7, name: 'Coke 375 mL', quantityInStock: 0, lowStockThreshold: 2, unit: '375 mL', ...overrides } as Product;
}

function localMachine(id: number, name: string): LocalMachine {
  return { id, name, isActive: true, isProviderNamed: false };
}

/**
 * The Adjust stock panel (issue #1156): a modal drawer (full screen on a phone) around the existing
 * adjustment form, which keeps the page behind it in place and closes only when nothing is saving.
 */
describe('StockAdjustmentPanelComponent (issue #1156)', () => {
  let showModal: jest.SpyInstance;

  beforeEach(() => {
    showModal = jest.spyOn(dialogPrototype, 'showModal');
  });

  afterEach(() => {
    jest.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  async function render(adjustResult: unknown = of({} as StockAdjustment)) {
    const adjust = jest.fn(() => adjustResult as never);
    await TestBed.configureTestingModule({
      imports: [StockAdjustmentPanelComponent],
      providers: [
        { provide: StockService, useValue: { adjust, restockCostSuggestion: jest.fn(() => of({ unitCost: 1, source: 'LastPurchase', purchaseDate: null })) } },
        { provide: SupportSessionService, useValue: { active$: of(false) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(StockAdjustmentPanelComponent);
    fixture.componentRef.setInput('product', product());
    fixture.componentRef.setInput('machines', [localMachine(41, 'Lobby')]);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const closed = jest.fn();
    const applied = jest.fn();
    fixture.componentInstance.closed.subscribe(closed);
    fixture.componentInstance.adjustmentApplied.subscribe(applied);
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, closed, applied, adjust, dialog: host.querySelector('dialog')! };
  }

  it('opens as a modal labelled by its heading, naming the product and its current stock', async () => {
    const { dialog, host } = await render();

    expect(showModal).toHaveBeenCalledTimes(1);
    const title = document.getElementById(dialog.getAttribute('aria-labelledby')!);
    expect(title?.textContent?.trim()).toBe('Adjust stock');
    const summary = host.querySelector('[data-testid="adjustment-panel-product"]')?.textContent?.replace(/\s+/g, ' ');
    expect(summary).toContain('Coke 375 mL');
    // A recorded zero is shown as zero, in units, not as the product's pack size.
    expect(summary).toContain('Current stock: 0 units');
  });

  it('moves focus to Quantity change when it opens', async () => {
    const { host } = await render();
    await Promise.resolve();

    expect(document.activeElement).toBe(host.querySelector('#adjustment-quantity-change'));
  });

  it('answers Escape, Close and Cancel by closing', async () => {
    const { dialog, host, closed } = await render();

    const escape = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(escape);
    expect(escape.defaultPrevented).toBe(true);
    host.querySelector<HTMLButtonElement>('[data-testid="adjustment-panel-close"]')!.click();
    host.querySelector<HTMLButtonElement>('[data-testid="cancel-adjustment"]')!.click();
    dialog.dispatchEvent(new MouseEvent('click', { bubbles: true }));

    expect(closed).toHaveBeenCalledTimes(4);
  });

  it('refuses to close while the adjustment is being saved, then reports it applied', async () => {
    const pending = new Subject<StockAdjustment>();
    const { fixture, dialog, host, closed, applied, adjust } = await render(pending.asObservable());
    const form = fixture.componentInstance;

    const quantity = host.querySelector<HTMLInputElement>('#adjustment-quantity-change')!;
    quantity.value = '-3';
    quantity.dispatchEvent(new Event('input'));
    const reason = host.querySelector<HTMLSelectElement>('#adjustment-reason')!;
    reason.value = reason.options[2].value; // Damaged
    reason.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="apply-adjustment"]')!.click();
    fixture.detectChanges();

    expect(adjust).toHaveBeenCalledWith(7, expect.objectContaining({ quantityChange: -3, reason: StockAdjustmentReason.Damaged }));
    expect(form.saving).toBe(true);
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    form.requestClose();
    expect(closed).not.toHaveBeenCalled();
    expect(host.querySelector<HTMLButtonElement>('[data-testid="apply-adjustment"]')!.disabled).toBe(true);

    pending.next({} as StockAdjustment);
    pending.complete();

    expect(applied).toHaveBeenCalledTimes(1);
    expect(form.saving).toBe(false);
  });

  it('leaves the top layer when it is removed', async () => {
    const close = jest.spyOn(dialogPrototype, 'close');
    const { fixture } = await render();

    fixture.destroy();

    expect(close).toHaveBeenCalled();
  });
});
