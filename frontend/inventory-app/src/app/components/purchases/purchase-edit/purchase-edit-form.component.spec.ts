import { TestBed } from '@angular/core/testing';
import { PurchaseEditFormComponent } from './purchase-edit-form.component';
import { PurchaseItemPayload, PurchaseUpdatePayload } from '../../../services/purchase.service';
import {
  GstClassification,
  GstClassificationSource,
  Product,
  Purchase,
  PurchaseItem,
  Supplier
} from '../../../models/models';

function item(overrides: Partial<PurchaseItem> = {}): PurchaseItem {
  return {
    id: 1,
    receiptId: 7,
    productId: 10,
    quantity: 2,
    unitCost: 1.1,
    gstClassification: GstClassification.Unknown,
    gstClassificationSource: GstClassificationSource.Unknown,
    ...overrides
  };
}

function purchase(overrides: Partial<Purchase> = {}): Purchase {
  return {
    id: 7,
    title: 'Weekly restock',
    notes: null,
    totalAmount: 10,
    deliveryCost: null,
    deliveryGstClassification: GstClassification.Unknown,
    deliveryGstClassificationSource: GstClassificationSource.Unknown,
    packageCost: null,
    packageGstClassification: GstClassification.Unknown,
    packageGstClassificationSource: GstClassificationSource.Unknown,
    purchaseDate: '2026-04-01T00:00:00Z',
    supplierId: null,
    supplier: null,
    fileName: 'invoice.pdf',
    storedFileName: 'abc.pdf',
    contentType: 'application/pdf',
    fileSizeBytes: 1024,
    createdAt: '2026-04-01T00:00:00Z',
    items: [item()],
    ...overrides
  };
}

function product(id: number, name: string): Product {
  return {
    id,
    name,
    unitPrice: 2,
    averageUnitCost: 1,
    machinePrice: null,
    commissionValue: null,
    suggestedNetValue: null,
    suggestedPriceValue: null,
    quantityInStock: 10,
    maxStockInMachine: 10,
    machineReplenishmentNeed: 0,
    onOrderQuantity: 0,
    lowStockThreshold: 2,
    restockTo: 10,
    needToOrder: 0,
    mdbCode: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isActive: true,
    isLowStock: false,
    isReorderAlert: false
  };
}

const PRODUCTS = [product(10, 'Coke'), product(20, 'Chips')];

/** The form component with a purchase already loaded into it, plus the payloads it emitted. */
function createForm(stored: Purchase, suppliers: Supplier[] = []) {
  const component = new PurchaseEditFormComponent();
  const saved: PurchaseUpdatePayload[] = [];
  const cancelled: number[] = [];
  component.saveRequested.subscribe((payload) => saved.push(payload));
  component.editCancelled.subscribe(() => cancelled.push(1));
  component.products = PRODUCTS;
  component.suppliers = suppliers;
  component.purchase = stored;

  return { component, saved, cancelled };
}

/** The line payloads as the server will actually read them, after JSON drops omitted fields. */
function savedItems(saved: PurchaseUpdatePayload[]): PurchaseItemPayload[] {
  expect(saved).toHaveLength(1);
  return JSON.parse(JSON.stringify(saved[0].items ?? [])) as PurchaseItemPayload[];
}

describe('PurchaseEditFormComponent field inventory (issue #475)', () => {
  it('opens with every stored editable field of the purchase it is given', () => {
    const stored = purchase({
      title: 'Weekly restock',
      notes: 'Price went up',
      totalAmount: 42.5,
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      supplierId: 3,
      items: [item({ id: 11, productId: 10, quantity: 4, unitCost: 2.25 })]
    });
    const { component } = createForm(stored);

    expect(component.editForm.title).toBe('Weekly restock');
    expect(component.editForm.notes).toBe('Price went up');
    expect(component.editForm.totalAmount).toBe(42.5);
    expect(component.editForm.deliveryCost).toBe(5);
    expect(component.editForm.deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(component.editForm.packageCost).toBe(2);
    expect(component.editForm.packageGstClassification).toBe(GstClassification.GstFree);
    expect(component.editForm.supplierId).toBe(3);
    expect(component.editForm.purchaseDate).not.toBe('');
    expect(component.editItems).toEqual([
      {
        id: 11,
        productId: 10,
        quantity: 4,
        unitCost: 2.25,
        gstClassification: GstClassification.Unknown,
        storedGstClassification: GstClassification.Unknown
      }
    ]);
  });

  it('keeps the line and subtotal arithmetic the inline editor used', () => {
    const { component } = createForm(purchase({ items: [item({ id: 11, quantity: 3, unitCost: 2 })] }));

    component.addEditItem();
    component.editItems[1].quantity = 2;
    component.editItems[1].unitCost = 1.5;

    expect(component.editLineTotal(component.editItems[0])).toBe(6);
    expect(component.editItemsSubtotal).toBe(9);
  });

  it('emits cancel without emitting a save', () => {
    const { component, saved, cancelled } = createForm(purchase());

    component.cancelEdit();

    expect(cancelled).toHaveLength(1);
    expect(saved).toHaveLength(0);
  });

  it('refuses to submit an empty title, exactly as the inline editor did', () => {
    const { component, saved } = createForm(purchase());

    component.editForm.title = '   ';
    component.submit();

    expect(saved).toHaveLength(0);
  });

  it('never emits a second save while one is still in flight', () => {
    const { component, saved } = createForm(purchase());

    component.submit();
    component.saving = true;
    component.submit();

    expect(saved).toHaveLength(1);
  });
});

describe('PurchaseEditFormComponent GST edit mapping (issue #431, moved by #475)', () => {
  it("shows each line's and each charge's stored classification when the form opens", () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      deliveryGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      items: [item({ id: 11, gstClassification: GstClassification.Taxable })]
    });
    const { component } = createForm(stored);

    expect(component.editForm.deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(component.editForm.packageGstClassification).toBe(GstClassification.GstFree);
    expect(component.editItems[0].gstClassification).toBe(GstClassification.Taxable);
    expect(component.editItems[0].id).toBe(11);
  });

  it('omits every classification the person did not change, so rule-derived provenance survives an unrelated edit', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      deliveryGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      packageGstClassificationSource: GstClassificationSource.SupplierFeeDefault,
      items: [item({ id: 11, gstClassification: GstClassification.Taxable })]
    });
    const { component, saved } = createForm(stored);

    component.editItems[0].quantity = 7;
    component.submit();

    expect(saved[0].deliveryGstClassification).toBeUndefined();
    expect(saved[0].packageGstClassification).toBeUndefined();
    expect(savedItems(saved)).toEqual([{ id: 11, productId: 10, quantity: 7, unitCost: 1.1 }]);
  });

  it('sends only the classifications the person changed', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Unknown,
      packageCost: 2,
      packageGstClassification: GstClassification.GstFree,
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Unknown }),
        item({ id: 12, productId: 20, gstClassification: GstClassification.Taxable })
      ]
    });
    const { component, saved } = createForm(stored);

    component.editForm.deliveryGstClassification = GstClassification.Taxable;
    component.editItems[1].gstClassification = GstClassification.GstFree;
    component.submit();

    expect(saved[0].deliveryGstClassification).toBe(GstClassification.Taxable);
    expect(saved[0].packageGstClassification).toBeUndefined();
    expect(savedItems(saved)).toEqual([
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1 },
      { id: 12, productId: 20, quantity: 2, unitCost: 1.1, gstClassification: GstClassification.GstFree }
    ]);
  });

  it('submits an explicit return to Not classified rather than omitting it', () => {
    const stored = purchase({ items: [item({ id: 11, gstClassification: GstClassification.Taxable })] });
    const { component, saved } = createForm(stored);

    component.editItems[0].gstClassification = GstClassification.Unknown;
    component.submit();

    expect(savedItems(saved)).toEqual([
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1, gstClassification: GstClassification.Unknown }
    ]);
  });

  it('keeps each line id when duplicate-product lines are reordered, and never substitutes the product or the position', () => {
    const stored = purchase({
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 10, gstClassification: GstClassification.GstFree })
      ]
    });
    const { component, saved } = createForm(stored);

    component.editItems.reverse();
    component.submit();

    expect(savedItems(saved)).toEqual([
      { id: 12, productId: 10, quantity: 2, unitCost: 1.1 },
      { id: 11, productId: 10, quantity: 2, unitCost: 1.1 }
    ]);
  });

  it("keeps the surviving line's own id when one of two duplicate-product lines is removed", () => {
    const stored = purchase({
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 10, gstClassification: GstClassification.GstFree })
      ]
    });
    const { component, saved } = createForm(stored);

    component.removeEditItem(0);
    component.submit();

    expect(savedItems(saved)).toEqual([{ id: 12, productId: 10, quantity: 2, unitCost: 1.1 }]);
  });

  it('omits the id of a line added during the edit and leaves it unclassified by default', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const { component, saved } = createForm(stored);

    component.addEditItem();
    component.submit();

    const items = savedItems(saved);
    expect(items[1].id).toBeUndefined();
    expect(items[1].gstClassification).toBeUndefined();
    expect(component.editItems[1].gstClassification).toBe(GstClassification.Unknown);
  });

  it("sends a new line's classification once the person picks one", () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const { component, saved } = createForm(stored);

    component.addEditItem();
    component.editItems[1].gstClassification = GstClassification.Taxable;
    component.submit();

    expect(savedItems(saved)[1].gstClassification).toBe(GstClassification.Taxable);
  });

  it('changes a stored line\'s product as a removal and a genuinely new line, with no id and no inherited classification', () => {
    const stored = purchase({
      items: [
        item({
          id: 11,
          productId: 10,
          gstClassification: GstClassification.Taxable,
          gstClassificationSource: GstClassificationSource.ProductRule
        })
      ]
    });
    const { component, saved } = createForm(stored);

    // The only way to record a different product: the stored line goes, the new product arrives as
    // its own line. Re-pointing line 11 at product 20 is what the server refuses.
    component.removeEditItem(0);
    component.addEditItem();
    component.editItems[0].productId = 20;
    component.editItems[0].quantity = 3;
    component.editItems[0].unitCost = 2.5;
    component.submit();

    expect(savedItems(saved)).toEqual([{ productId: 20, quantity: 3, unitCost: 2.5 }]);
    expect(component.editItems[0].gstClassification).toBe(GstClassification.Unknown);
  });

  it('treats a stored line as identified and a line added during the edit as new', () => {
    const stored = purchase({ items: [item({ id: 11 })] });
    const { component } = createForm(stored);

    component.addEditItem();

    expect(component.isStoredLine(component.editItems[0])).toBe(true);
    expect(component.isStoredLine(component.editItems[1])).toBe(false);
  });

  it('never submits a classification for a charge the person cleared', () => {
    const stored = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      packageCost: 2,
      packageGstClassification: GstClassification.Taxable
    });
    const { component, saved } = createForm(stored);

    component.editForm.deliveryCost = null;
    component.editForm.packageCost = 0;
    component.submit();

    expect(saved[0].deliveryGstClassification).toBeUndefined();
    expect(saved[0].packageGstClassification).toBeUndefined();
  });

  it('hides a charge picker while the charge has no value', () => {
    const { component } = createForm(purchase());

    expect(component.hasCharge(null)).toBe(false);
    expect(component.hasCharge(0)).toBe(false);
    expect(component.hasCharge(5)).toBe(true);
  });

  it('keeps an unchanged purchase date as the stored instant rather than shifting it', () => {
    const stored = purchase({ purchaseDate: '2026-04-01T03:30:00Z' });
    const { component, saved } = createForm(stored);

    component.submit();

    expect(saved[0].purchaseDate).toBe(new Date('2026-04-01T03:30:00Z').toISOString());
  });
});

describe('PurchaseEditFormComponent template (issue #475)', () => {
  const render = async (stored: Purchase, error = '', saving = false) => {
    await TestBed.configureTestingModule({ imports: [PurchaseEditFormComponent] }).compileComponents();

    const fixture = TestBed.createComponent(PurchaseEditFormComponent);
    fixture.componentInstance.products = PRODUCTS;
    fixture.componentInstance.suppliers = [];
    fixture.componentInstance.error = error;
    fixture.componentInstance.saving = saving;
    fixture.componentInstance.purchase = stored;
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  };

  afterEach(() => TestBed.resetTestingModule());

  it('offers no product picker for a stored line and explains the remove/add workflow instead', async () => {
    const { host } = await render(purchase({ items: [item({ id: 11, productId: 10 })] }));

    expect(host.querySelectorAll('[data-testid="edit-purchase-item-product"]')).toHaveLength(0);
    const fixedProduct = host.querySelector('[data-testid="edit-purchase-item-product-fixed"]');
    expect(fixedProduct?.textContent).toContain('Coke');
    expect(fixedProduct?.textContent).toContain('Product cannot be changed');
    expect(fixedProduct?.textContent).toContain('Remove this line and add the new product as its own line');
  });

  it('offers a product picker for a line added during the edit', async () => {
    const { fixture, host } = await render(purchase({ items: [item({ id: 11, productId: 10 })] }));

    fixture.componentInstance.addEditItem();
    fixture.detectChanges();

    expect(host.querySelectorAll('[data-testid="edit-purchase-item-product"]')).toHaveLength(1);
    expect(host.querySelectorAll('[data-testid="edit-purchase-item"]')).toHaveLength(2);
  });

  it('shows a charge GST picker only while that charge has a value', async () => {
    const { fixture, host } = await render(purchase({ deliveryCost: 5, packageCost: null }));

    expect(host.querySelector('[data-testid="edit-delivery-gst"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="edit-package-gst"]')).toBeNull();

    fixture.componentInstance.editForm.packageCost = 2;
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="edit-package-gst"]')).not.toBeNull();
  });

  it("renders the save error it is given, keeping the person's entered values on the page", async () => {
    const { host } = await render(
      purchase({ items: [item({ id: 11 })] }),
      'A GST classification must be one of Unknown, Taxable, GstFree.'
    );

    expect(host.querySelector('[data-testid="edit-purchase-error"]')?.textContent).toContain(
      'A GST classification must be one of'
    );
    expect(host.querySelectorAll('[data-testid="edit-purchase-item"]')).toHaveLength(1);
  });

  it('disables the Save button while a save is in flight, so it cannot be pressed twice', async () => {
    const { host } = await render(purchase(), '', true);

    const save = host.querySelector('[data-testid="edit-purchase-save"]') as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });

  /**
   * The responsive rules are the ones `/purchases/new` already uses and that issue #410's restyle
   * verified at 1440px and 390px: a single column of fields that becomes two at `md`, and a
   * line-item row that is one column per control below `md`. This pins the classes rather than
   * re-deciding the layout.
   */
  it('reuses the purchase entry form\'s responsive grids, so it collapses to one column on a narrow screen', async () => {
    const { host } = await render(purchase({ items: [item({ id: 11 })] }));

    expect(host.querySelector('.grid.gap-4.md\\:grid-cols-2')).not.toBeNull();
    const line = host.querySelector('[data-testid="edit-purchase-item"]');
    expect(line?.className).toContain('grid');
    expect(line?.className).toContain('md:grid-cols-[minmax(0,3fr)_minmax(7rem,1fr)_minmax(9rem,1fr)_minmax(9rem,1fr)_auto]');
  });

  it('labels every field it offers', async () => {
    const { host } = await render(purchase({ deliveryCost: 5, packageCost: 2 }));

    const labelled = ['editTitle', 'editSupplierId', 'editPurchaseDate', 'editTotalAmount', 'editDeliveryCost', 'editPackageCost', 'editNotes'];
    for (const id of labelled) {
      expect(host.querySelector(`label[for="${id}"]`)).not.toBeNull();
      expect(host.querySelector(`#${id}`)).not.toBeNull();
    }
  });
});
