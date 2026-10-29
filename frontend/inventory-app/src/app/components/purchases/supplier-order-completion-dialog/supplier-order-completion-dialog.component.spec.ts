import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SupplierOrderCompletionDialogComponent } from './supplier-order-completion-dialog.component';
import { SupplierOrderCompletionPreview, SupplierOrderStatus } from '../../../models/models';

/**
 * The prototype `<dialog>`'s methods live on, taken from an element rather than from the global so
 * the spec works whichever interface jsdom maps `dialog` to - the same arrangement
 * `cheapest-supplier-cell.component.spec.ts` uses. What is spied on is which of the two opening
 * calls the component makes: `showModal()` is the one that makes the page behind inert.
 */
const dialogPrototype = Object.getPrototypeOf(document.createElement('dialog')) as HTMLDialogElement;

function preview(overrides: Partial<SupplierOrderCompletionPreview> = {}): SupplierOrderCompletionPreview {
  return {
    supplierOrderId: 9,
    status: SupplierOrderStatus.PartiallyReceived,
    eligibleForCompletion: true,
    hasOutstandingBalance: true,
    decisionFingerprint: 'fingerprint-from-the-server',
    outstandingLines: [
      {
        supplierOrderLineId: 1,
        productId: 5,
        productName: 'Coke 375mL',
        unit: 'can',
        quantityOrdered: 24,
        quantityAlreadyReceived: 6,
        quantityOnThisPurchase: 10,
        outstandingQuantity: 8
      },
      {
        supplierOrderLineId: 2,
        productId: 7,
        productName: 'Chips 50g',
        unit: null,
        quantityOrdered: 10,
        quantityAlreadyReceived: 0,
        quantityOnThisPurchase: 0,
        outstandingQuantity: 10
      }
    ],
    ...overrides
  };
}

describe('SupplierOrderCompletionDialogComponent (issue #974)', () => {
  let showModal: jest.SpyInstance;
  let show: jest.SpyInstance;

  beforeEach(() => {
    showModal = jest.spyOn(dialogPrototype, 'showModal');
    show = jest.spyOn(dialogPrototype, 'show');
  });

  afterEach(() => {
    jest.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  async function render(choice: SupplierOrderCompletionPreview | null = preview()) {
    await TestBed.configureTestingModule({
      imports: [SupplierOrderCompletionDialogComponent]
    }).compileComponents();

    const fixture: ComponentFixture<SupplierOrderCompletionDialogComponent> =
      TestBed.createComponent(SupplierOrderCompletionDialogComponent);
    fixture.componentInstance.choice = choice;
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const button = (text: string) =>
      Array.from(host.querySelectorAll('button')).find((candidate) => candidate.textContent?.trim() === text) as
        | HTMLButtonElement
        | undefined;

    return { fixture, host, button };
  }

  it('renders nothing at all until there is a choice to make', async () => {
    const { host } = await render(null);

    expect(host.querySelector('dialog')).toBeNull();
    expect(showModal).not.toHaveBeenCalled();
  });

  it('opens as a real modal, so the purchase form behind it is inert', async () => {
    await render();

    expect(showModal).toHaveBeenCalledTimes(1);
    expect(show).not.toHaveBeenCalled();
  });

  /**
   * The products that will be short, and how short, in the units they were ordered in. A product
   * with no recorded unit shows the bare quantity rather than an invented label.
   */
  it('names every affected product with its outstanding quantity and unit', async () => {
    const { host } = await render();

    const rows = Array.from(host.querySelectorAll('[data-testid="completion-outstanding-row"]'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Coke 375mL');
    expect(rows[0].textContent).toContain('8 can');
    expect(rows[1].textContent).toContain('Chips 50g');
    expect(rows[1].textContent).toContain('10');
  });

  it('labels the dialog by its own heading', async () => {
    const { host } = await render();

    const dialog = host.querySelector('dialog') as HTMLDialogElement;
    const headingId = dialog.getAttribute('aria-labelledby');
    expect(headingId).toBeTruthy();
    expect(host.querySelector(`#${headingId}`)?.textContent).toBeTruthy();
  });

  /** Keep open is the safe default, so it is the control that holds focus when the dialog opens. */
  it('offers keeping the order open as the focused default', async () => {
    const { button } = await render();

    expect(button('Keep order open')).toBeDefined();
    expect(document.activeElement).toBe(button('Keep order open'));
  });

  it('emits the keep-open choice', async () => {
    const { fixture, button } = await render();
    const keepOpen = jest.fn();
    fixture.componentInstance.keepOpen.subscribe(keepOpen);

    button('Keep order open')!.click();

    expect(keepOpen).toHaveBeenCalledTimes(1);
  });

  it('emits the complete choice', async () => {
    const { fixture, button } = await render();
    const complete = jest.fn();
    fixture.componentInstance.complete.subscribe(complete);

    button('Complete order')!.click();

    expect(complete).toHaveBeenCalledTimes(1);
  });

  it('emits the cancel choice from the Back to purchase control', async () => {
    const { fixture, button } = await render();
    const cancelled = jest.fn();
    fixture.componentInstance.cancelled.subscribe(cancelled);

    button('Back to purchase')!.click();

    expect(cancelled).toHaveBeenCalledTimes(1);
  });

  /**
   * The browser's own close request - Escape, most obviously - is the cancel choice, never a
   * silent keep-open or complete: dismissing a question is not answering it.
   */
  it('treats the browsers close request as cancelling', async () => {
    const { fixture, host } = await render();
    const cancelled = jest.fn();
    const complete = jest.fn();
    const keepOpen = jest.fn();
    fixture.componentInstance.cancelled.subscribe(cancelled);
    fixture.componentInstance.complete.subscribe(complete);
    fixture.componentInstance.keepOpen.subscribe(keepOpen);

    (host.querySelector('dialog') as HTMLDialogElement)
      .dispatchEvent(new Event('cancel', { cancelable: true }));

    expect(cancelled).toHaveBeenCalledTimes(1);
    expect(complete).not.toHaveBeenCalled();
    expect(keepOpen).not.toHaveBeenCalled();
  });

  it('closes the dialog when the choice is withdrawn', async () => {
    const { fixture, host } = await render();
    const close = jest.spyOn(host.querySelector('dialog') as HTMLDialogElement, 'close');

    fixture.componentInstance.choice = null;
    fixture.detectChanges();

    expect(close).toHaveBeenCalled();
    expect(host.querySelector('dialog')).toBeNull();
  });
});
