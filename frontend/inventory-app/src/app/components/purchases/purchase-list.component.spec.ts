import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { PURCHASE_DEFAULT_PAGE_SIZE, PurchaseListComponent } from './purchase-list.component';
import { PurchaseService } from '../../services/purchase.service';
import { SupportSessionService } from '../../support-session/support-session.service';
import {
  GstClassification,
  GstClassificationSource,
  Purchase,
  PurchaseGstSummary,
  PurchaseItem,
  PurchaseValidation
} from '../../models/models';

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

/** A purchase recorded with no supporting document at all (issue #976): all four values absent. */
function withoutDocument(overrides: Partial<Purchase> = {}): Purchase {
  return purchase({
    fileName: null,
    storedFileName: null,
    contentType: null,
    fileSizeBytes: null,
    ...overrides
  });
}

async function render(
  purchases: Purchase[],
  gst: PurchaseGstSummary | null = null,
  validation: PurchaseValidation | null = null,
  sessionActive: Observable<boolean> = of(false),
  overrides: { getAll?: jest.Mock; delete?: jest.Mock } = {}
) {
  const getAll = overrides.getAll ?? jest.fn(() => of(purchases));
  const deleteFn = overrides.delete ?? jest.fn(() => of(undefined));
  const attachDocument = jest.fn(() => of(purchases[0]));
  await TestBed.configureTestingModule({
    imports: [PurchaseListComponent],
    providers: [
      provideRouter([]),
      {
        provide: PurchaseService,
        useValue: {
          getAll,
          delete: deleteFn,
          getFile: jest.fn(() => of(new Blob())),
          attachDocument,
          getValidationFor: jest.fn(() => validation),
          getGstSummaryFor: jest.fn(() => gst)
        }
      },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(PurchaseListComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, getAll, attachDocument, delete: deleteFn };
}

afterEach(() => TestBed.resetTestingModule());

describe('PurchaseListComponent edit navigation (issue #475)', () => {
  it('links Edit to the dedicated edit page for that purchase instead of expanding an editor', async () => {
    const { host } = await render([purchase({ id: 7 }), purchase({ id: 9, title: 'Second' })]);

    const links = Array.from(host.querySelectorAll('[data-testid="purchase-edit-link"]'));
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/purchases/7/edit', '/purchases/9/edit']);
  });

  it('expands no editor in the table at all: no edit form, no edit row, no edit controls', async () => {
    const { host } = await render([purchase({ deliveryCost: 5, packageCost: 2 })]);

    expect(host.querySelectorAll('form')).toHaveLength(0);
    expect(host.querySelectorAll('[data-testid="edit-purchase-item"]')).toHaveLength(0);
    expect(host.querySelector('[data-testid="edit-delivery-gst"]')).toBeNull();
    expect(host.querySelector('[data-testid="edit-package-gst"]')).toBeNull();
    expect(host.querySelector('[data-testid="edit-purchase-error"]')).toBeNull();
    expect(host.querySelectorAll('tbody > tr')).toHaveLength(1);
  });

  it('gives the Edit action an accessible name naming the purchase it opens', async () => {
    const { host } = await render([purchase({ id: 7, title: 'Weekly restock' })]);

    expect(host.querySelector('[data-testid="purchase-edit-link"]')?.getAttribute('aria-label')).toBe(
      'Edit Weekly restock'
    );
  });
});

describe('PurchaseListComponent GST display (issue #431)', () => {
  it("shows the API's input GST, every component's classification and the unresolved state together", async () => {
    const mixed = purchase({
      deliveryCost: 5,
      deliveryGstClassification: GstClassification.Taxable,
      packageCost: 2,
      packageGstClassification: GstClassification.Unknown,
      items: [
        item({ id: 11, productId: 10, gstClassification: GstClassification.Taxable }),
        item({ id: 12, productId: 20, gstClassification: GstClassification.GstFree })
      ]
    });
    const { host } = await render([mixed], { inputGst: 0.65, unresolvedComponentCount: 1, unresolvedAmount: 2 });

    const summary = host.querySelector('[data-testid="purchase-gst-summary"]');
    expect(summary?.textContent).toContain('$0.65');
    expect(summary?.textContent).toContain('Delivery: Taxable');
    expect(summary?.textContent).toContain('Package: Not classified');
    expect(host.querySelector('[data-testid="purchase-gst-unresolved"]')?.textContent).toContain('$2.00');

    const lines = host.querySelectorAll('[data-testid="purchase-item-line"]');
    expect(lines[0].textContent).toContain('Taxable');
    expect(lines[1].textContent).toContain('GST-free');
  });

  it('names no classification for a charge that was never entered, and reports nothing unresolved for it', async () => {
    const complete = purchase({
      deliveryCost: null,
      packageCost: 0,
      items: [item({ id: 11, gstClassification: GstClassification.GstFree })]
    });
    const { host } = await render([complete], { inputGst: 0, unresolvedComponentCount: 0, unresolvedAmount: 0 });

    const summary = host.querySelector('[data-testid="purchase-gst-summary"]');
    expect(summary?.textContent).not.toContain('Delivery:');
    expect(summary?.textContent).not.toContain('Package:');
    expect(host.querySelector('[data-testid="purchase-gst-unresolved"]')).toBeNull();
  });
});

describe('PurchaseListComponent invoice number (issue #1162)', () => {
  const headings = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('thead th')).map((cell) => cell.textContent?.replace(/\s+/g, ' ').trim());

  it('has an Invoice number column that shows the number a purchase records', async () => {
    const { host } = await render([purchase({ invoiceNumber: 'INV-123' })]);

    expect(headings(host)).toContain('Invoice number');
    expect(host.querySelector('[data-testid="purchase-invoice-number"]')?.textContent?.trim()).toBe('INV-123');
  });

  it('shows a purchase that records none exactly as before, with no number in its row', async () => {
    const { host } = await render([purchase({ invoiceNumber: null }), purchase({ id: 9 })]);

    expect(headings(host)).toContain('Invoice number');
    expect(host.querySelectorAll('[data-testid="purchase-invoice-number"]')).toHaveLength(0);
    expect(host.querySelectorAll('[data-testid="purchase-no-invoice-number"]')).toHaveLength(2);
    expect(host.querySelectorAll('tbody > tr.table-row')).toHaveLength(2);
  });
});

describe('PurchaseListComponent consolidated row layout (issue #474)', () => {
  it('renders one display row per purchase even when notes, GST and a total mismatch warning are all present', async () => {
    const withEverything = purchase({
      notes: 'Call supplier before reordering - price went up a lot this time.',
      totalAmount: 20
    });
    const { host } = await render(
      [withEverything],
      { inputGst: 0.65, unresolvedComponentCount: 0, unresolvedAmount: 0 },
      { hasTotalMismatch: true, calculatedTotal: 18, totalDifference: 2 }
    );

    const displayRows = host.querySelectorAll('tbody > tr.table-row');
    expect(displayRows).toHaveLength(1);

    const row = displayRows[0];
    expect(row.textContent).toContain('Call supplier before reordering');
    expect(row.textContent).toContain('$0.65');
    expect(row.textContent).toContain('Does not match calculated total');
    expect(row.querySelector('[data-testid="purchase-gst-summary"]')).not.toBeNull();
  });

  it('renders one plain display row for a purchase with no notes, GST summary or mismatch', async () => {
    const { host } = await render([purchase({ notes: null })]);

    expect(host.querySelectorAll('tbody > tr.table-row')).toHaveLength(1);
  });
});

describe('PurchaseListComponent Actions column layout (issue #449)', () => {
  const wide = () =>
    purchase({
      items: [item({ id: 11, productId: 10 }), item({ id: 12, productId: 20 }), item({ id: 13, productId: 10 })]
    });

  it('keeps the Actions header pinned to the right edge of the horizontally scrollable table', async () => {
    const { host } = await render([wide()]);

    const header = host.querySelector('[data-testid="purchases-actions-header"]');
    expect(header).not.toBeNull();
    expect(header?.classList.contains('sticky')).toBe(true);
    expect(header?.classList.contains('right-0')).toBe(true);
  });

  it('keeps both Edit and Delete reachable in a sticky Actions cell, independent of how wide the Items column grows', async () => {
    const { host } = await render([wide()]);

    const actionsCell = host.querySelector('[data-testid="purchases-actions-cell"]');
    expect(actionsCell).not.toBeNull();
    expect(actionsCell?.classList.contains('sticky')).toBe(true);
    expect(actionsCell?.classList.contains('right-0')).toBe(true);

    expect(actionsCell?.querySelector('[data-testid="purchase-edit-link"]')?.textContent).toContain('Edit');
    const deleteButton = Array.from(actionsCell?.querySelectorAll('button') ?? []).find((b) =>
      b.textContent?.includes('Delete')
    );
    expect(deleteButton).toBeDefined();
  });

  it('leaves remove wired to the Delete button unchanged', async () => {
    const stored = purchase({ items: [item({ id: 11, productId: 10 })] });
    const { fixture, host } = await render([stored]);

    const removeSpy = jest.spyOn(fixture.componentInstance, 'remove').mockImplementation(() => undefined);
    const actionsCell = host.querySelector('[data-testid="purchases-actions-cell"]');
    const deleteButton = Array.from(actionsCell?.querySelectorAll('button') ?? []).find((b) =>
      b.textContent?.includes('Delete')
    ) as HTMLButtonElement;

    deleteButton.click();

    expect(removeSpy).toHaveBeenCalledWith(stored);
  });
});

/**
 * A purchase with no supporting document (issue #976). "No document" is a real state the page has
 * to report, and the purchase that has none is where the offer to add one belongs.
 */
describe('PurchaseListComponent purchases with no document (issue #976)', () => {
  it('reports "No document" and offers to add one, rather than leaving the cell blank', async () => {
    const { host } = await render([withoutDocument()]);

    expect(host.querySelector('[data-testid="purchase-no-document"]')?.textContent).toContain('No document');
    expect(host.querySelector('[data-testid="purchase-document-name"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-document-attach"]')).not.toBeNull();
  });

  it('offers no way to open a document that is not there', async () => {
    const { host } = await render([withoutDocument()]);

    expect(host.querySelector('[aria-label="Open document for Weekly restock"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-no-document-placeholder"]')).not.toBeNull();
  });

  it('requests no thumbnail for a purchase with no document', async () => {
    await render([withoutDocument()]);

    expect(TestBed.inject(PurchaseService).getFile).not.toHaveBeenCalled();
  });

  it('shows the document name and size, and no "Add document", for a purchase that has one', async () => {
    const { host } = await render([purchase()]);

    expect(host.querySelector('[data-testid="purchase-document-name"]')?.textContent).toContain('invoice.pdf');
    expect(host.querySelector('[data-testid="purchase-document-name"]')?.textContent).toContain('1.0 KB');
    expect(host.querySelector('[data-testid="purchase-no-document"]')).toBeNull();
    expect(host.querySelector('[data-testid="purchase-document-attach"]')).toBeNull();
    expect(host.querySelector('[aria-label="Open document for Weekly restock"]')).not.toBeNull();
  });

  /**
   * Adding a document is a write, so it is hidden while a support session is active for exactly the
   * reason Delete is: the session grants `PurchasingOperate`, and the backend refuses the write by
   * its own read-only method rule rather than by withholding the capability.
   */
  it('hides "Add document" while a support session is active, and still says there is none', async () => {
    const { host } = await render([withoutDocument()], null, null, of(true));

    expect(host.querySelector('[data-testid="purchase-no-document"]')?.textContent).toContain('No document');
    expect(host.querySelector('[data-testid="purchase-document-attach"]')).toBeNull();
  });

  it('reloads the list once a document has been added, so the row stops saying there is none', async () => {
    const { fixture, getAll } = await render([withoutDocument()]);
    expect(getAll).toHaveBeenCalledTimes(1);

    fixture.componentInstance.load();

    expect(getAll).toHaveBeenCalledTimes(2);
  });
});

/**
 * Add Purchase and Delete while a support session is active (issue #657, extending #554). A
 * session grants `PurchasingOperate`, so these would otherwise still render - hiding them is this
 * page's own job, not the capability check's.
 */
describe('PurchaseListComponent write controls during a support session (issue #657)', () => {
  it('hides Add Purchase and Delete while a session is active', async () => {
    const { host } = await render([purchase({ id: 7 })], null, null, of(true));

    expect(host.querySelector('a[href="/purchases/new"]')).toBeNull();
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.includes('Delete'));
    expect(deleteButton).toBeUndefined();
  });

  it('shows Add Purchase and Delete while no session is active', async () => {
    const { host } = await render([purchase({ id: 7 })], null, null, of(false));

    expect(host.querySelector('a[href="/purchases/new"]')).not.toBeNull();
    const deleteButton = Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.includes('Delete'));
    expect(deleteButton).toBeDefined();
  });

  it('does not delete the purchase while a session is active, even if the write method is invoked directly', async () => {
    const { fixture } = await render([purchase({ id: 7 })], null, null, of(true));
    const component = fixture.componentInstance;
    const deleteSpy = jest.spyOn(TestBed.inject(PurchaseService), 'delete');
    const confirmSpy = jest.spyOn(window, 'confirm');

    component.remove(purchase({ id: 7 }));

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(deleteSpy).not.toHaveBeenCalled();
  });
});

/**
 * Pagination over the one existing `getAll` read (issue #1209), reusing `ListPaginationComponent`
 * and `paging.ts` the same way All Products (#1093) and the Expenses register (#1170) do: a page
 * is a window onto the already-fetched list, so turning a page or choosing a size re-requests
 * nothing, and the thumbnail downloads (`loadThumbnails`) follow onto only the page shown.
 */
describe('PurchaseListComponent pagination (issue #1209)', () => {
  const originalCreateObjectURL = URL.createObjectURL;
  const originalRevokeObjectURL = URL.revokeObjectURL;

  beforeEach(() => {
    let next = 0;
    (URL as unknown as { createObjectURL: (blob: Blob) => string }).createObjectURL = () => `blob:purchase-${(next += 1)}`;
    (URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = jest.fn();
  });

  afterEach(() => {
    // Destroys the fixture (which revokes through the stub) before the stub is removed: the
    // top-level `afterEach` that resets the testing module runs after this one, by which point
    // `URL.revokeObjectURL` would otherwise be back to jsdom's own (absent) implementation.
    TestBed.resetTestingModule();
    URL.createObjectURL = originalCreateObjectURL;
    URL.revokeObjectURL = originalRevokeObjectURL;
  });

  const many = (count: number) =>
    Array.from({ length: count }, (_, index) =>
      purchase({ id: index + 1, title: `Purchase ${String(index + 1).padStart(2, '0')}` })
    );
  const image = (id: number, title: string) => purchase({ id, title, contentType: 'image/png' });
  const titles = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('tbody > tr.table-row strong')).map((el) => el.textContent?.trim());

  it('shows ten purchases per page by default and offers exactly 5, 10, 20, 50 and 100', async () => {
    const { host } = await render(many(25));

    expect(PURCHASE_DEFAULT_PAGE_SIZE).toBe(10);
    expect(titles(host)).toHaveLength(10);
    const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => Number(option.value))).toEqual([5, 10, 20, 50, 100]);
    expect(host.querySelector('[data-testid="list-pagination-range"]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Showing 1–10 of 25 purchases'
    );
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent?.trim()).toBe('Page 1 of 3');
  });

  it('turns pages without missing or repeating a purchase, in the order the API returned, and requests nothing more', async () => {
    const purchases = many(25);
    const { fixture, host, getAll } = await render(purchases);
    const before = getAll.mock.calls.length;
    const seen: (string | undefined)[] = [...titles(host)];

    for (let page = 1; page < 3; page += 1) {
      (host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      seen.push(...titles(host));
    }

    expect(seen).toEqual(purchases.map((p) => p.title));
    expect((host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).disabled).toBe(true);
    expect(getAll.mock.calls).toHaveLength(before);
  });

  it('returns to page 1 when the page size changes, without requesting the list again', async () => {
    const { fixture, host, getAll } = await render(many(25));
    fixture.componentInstance.onPageIndexChange(2);
    fixture.detectChanges();
    const before = getAll.mock.calls.length;

    const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
    select.value = '5';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(fixture.componentInstance.pageIndex).toBe(0);
    expect(titles(host)).toEqual(['Purchase 01', 'Purchase 02', 'Purchase 03', 'Purchase 04', 'Purchase 05']);
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent?.trim()).toBe('Page 1 of 5');
    expect(getAll.mock.calls).toHaveLength(before);
  });

  it('shows the single page for exactly one purchase, with Previous and Next both disabled', async () => {
    const { host } = await render([purchase({ id: 1 })]);

    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent?.trim()).toBe('Page 1 of 1');
    expect((host.querySelector('[data-testid="list-pagination-previous"]') as HTMLButtonElement).disabled).toBe(true);
    expect((host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows a partial last page: 11 purchases at 10 per page leaves one on page 2', async () => {
    const { fixture, host } = await render(many(11));
    (host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(titles(host)).toEqual(['Purchase 11']);
    expect(host.querySelector('[data-testid="list-pagination-range"]')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Showing 11–11 of 11 purchases'
    );
  });

  it('shows no pager alongside the empty-state card when there are no purchases', async () => {
    const { host } = await render([]);

    expect(host.textContent).toContain('No purchases recorded yet.');
    expect(host.querySelector('[data-testid="list-pagination"]')).toBeNull();
  });

  it('keeps the current page across a reload (Delete, Add document) that still contains it', async () => {
    const purchases = many(25);
    const { fixture, host } = await render(purchases);
    const component = fixture.componentInstance;
    component.onPageIndexChange(1);
    fixture.detectChanges();

    component.load();
    fixture.detectChanges();

    expect(component.pageIndex).toBe(1);
    expect(titles(host)).toEqual(purchases.slice(10, 20).map((p) => p.title));
  });

  it('moves to the previous page when a Delete empties the last page', async () => {
    const purchases = many(11);
    const getAll = jest.fn().mockReturnValueOnce(of(purchases)).mockReturnValueOnce(of(purchases.slice(0, 10)));
    const { fixture, host } = await render(purchases, null, null, of(false), { getAll });
    const component = fixture.componentInstance;
    component.onPageIndexChange(1);
    fixture.detectChanges();
    expect(titles(host)).toEqual(['Purchase 11']);

    jest.spyOn(window, 'confirm').mockReturnValue(true);
    component.remove(purchases[10]);
    fixture.detectChanges();

    expect(component.pageIndex).toBe(0);
    expect(titles(host)).toHaveLength(10);
  });

  it('fetches thumbnails only for image purchases on the visible page, and the new page’s when it turns', async () => {
    const pageOne = Array.from({ length: 10 }, (_, i) => image(i + 1, `Purchase ${String(i + 1).padStart(2, '0')}`));
    const pageTwo = [image(11, 'Purchase 11'), withoutDocument({ id: 12, title: 'Purchase 12' })];
    const { fixture, host } = await render([...pageOne, ...pageTwo]);
    const getFile = TestBed.inject(PurchaseService).getFile as jest.Mock;

    expect(getFile).toHaveBeenCalledTimes(10);
    expect(getFile.mock.calls.map((call) => call[0]).sort((a, b) => a - b)).toEqual(pageOne.map((p) => p.id));

    getFile.mockClear();
    (host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(getFile).toHaveBeenCalledTimes(1);
    expect(getFile).toHaveBeenCalledWith(11);
  });

  it('releases the previous page’s object URLs when a page turns', async () => {
    const pageOne = Array.from({ length: 10 }, (_, i) => image(i + 1, `Purchase ${String(i + 1).padStart(2, '0')}`));
    const pageTwo = [image(11, 'Purchase 11')];
    const { fixture, host } = await render([...pageOne, ...pageTwo]);
    const revoke = URL.revokeObjectURL as jest.Mock;
    revoke.mockClear();

    (host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(revoke).toHaveBeenCalledTimes(10);
  });

  it('releases every object URL when leaving the page', async () => {
    const images = Array.from({ length: 3 }, (_, i) => image(i + 1, `Purchase ${String(i + 1).padStart(2, '0')}`));
    const { fixture } = await render(images);
    const revoke = URL.revokeObjectURL as jest.Mock;
    revoke.mockClear();

    fixture.destroy();

    expect(revoke).toHaveBeenCalledTimes(3);
  });
});
