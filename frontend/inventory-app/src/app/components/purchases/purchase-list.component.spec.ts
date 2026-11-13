import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';
import { PurchaseListComponent } from './purchase-list.component';
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
  sessionActive: Observable<boolean> = of(false)
) {
  const getAll = jest.fn(() => of(purchases));
  const attachDocument = jest.fn(() => of(purchases[0]));
  await TestBed.configureTestingModule({
    imports: [PurchaseListComponent],
    providers: [
      provideRouter([]),
      {
        provide: PurchaseService,
        useValue: {
          getAll,
          delete: jest.fn(() => of(undefined)),
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
  return { fixture, host: fixture.nativeElement as HTMLElement, getAll, attachDocument };
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
 * Paging (issue #1209): the list is read once and paged in the browser with the shared
 * `ListPaginationComponent`. The list never downloads a receipt itself (issue #1234): rows show an
 * attachment icon built from list metadata, and a file is fetched only once the operator opens it.
 */
describe('PurchaseListComponent pagination (issue #1209)', () => {
  const originalCreate = URL.createObjectURL;
  const originalRevoke = URL.revokeObjectURL;
  let created = 0;

  beforeEach(() => {
    created = 0;
    URL.createObjectURL = jest.fn(() => `blob:opened-${++created}`);
    URL.revokeObjectURL = jest.fn();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    URL.createObjectURL = originalCreate;
    URL.revokeObjectURL = originalRevoke;
  });

  /** `count` purchases with ids 1..count, newest first as the API returns them. */
  function many(count: number, overrides: (id: number) => Partial<Purchase> = () => ({})): Purchase[] {
    return Array.from({ length: count }, (_, i) =>
      purchase({ id: i + 1, title: `Purchase ${i + 1}`, ...overrides(i + 1) })
    );
  }

  function image(id: number): Partial<Purchase> {
    return { fileName: `receipt-${id}.jpg`, storedFileName: `s-${id}.jpg`, contentType: 'image/jpeg' };
  }

  function rowTitles(host: HTMLElement): string[] {
    return Array.from(host.querySelectorAll('tbody > tr strong')).map((el) => el.textContent?.trim() ?? '');
  }

  function rangeText(host: HTMLElement): string {
    return (host.querySelector('[data-testid="list-pagination-range"]')?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function click(host: HTMLElement, testId: string): void {
    (host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
  }

  function chooseSize(host: HTMLElement, size: number): void {
    const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
    select.value = String(size);
    select.dispatchEvent(new Event('change'));
  }

  function fileIds(): number[] {
    return (TestBed.inject(PurchaseService).getFile as jest.Mock).mock.calls.map(([id]) => id as number);
  }

  it('shows no pager with no purchases, only the existing empty state', async () => {
    const { host } = await render([]);

    expect(host.textContent).toContain('No purchases recorded yet.');
    expect(host.querySelector('[data-testid="list-pagination"]')).toBeNull();
  });

  it('shows a single purchase on one page with the range "1–1 of 1"', async () => {
    const { host } = await render(many(1));

    expect(rowTitles(host)).toEqual(['Purchase 1']);
    expect(rangeText(host)).toBe('Showing 1–1 of 1 purchases');
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toBe('Page 1 of 1');
    expect((host.querySelector('[data-testid="list-pagination-previous"]') as HTMLButtonElement).disabled).toBe(true);
    expect((host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('starts on page 1 at 10 per page and offers 5, 10, 20, 50 and 100', async () => {
    const { host } = await render(many(10));

    expect(rowTitles(host)).toHaveLength(10);
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toBe('Page 1 of 1');
    const select = host.querySelector('[data-testid="list-pagination-size"]') as HTMLSelectElement;
    expect(Array.from(select.options).map((o) => Number(o.value))).toEqual([5, 10, 20, 50, 100]);
    expect(select.value).toBe('10');
  });

  it('puts the eleventh purchase on a second page, with a partial range', async () => {
    const { fixture, host } = await render(many(11));

    expect(rowTitles(host)).toHaveLength(10);
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toBe('Page 1 of 2');

    click(host, 'list-pagination-next');
    fixture.detectChanges();

    expect(rowTitles(host)).toEqual(['Purchase 11']);
    expect(rangeText(host)).toBe('Showing 11–11 of 11 purchases');
    expect((host.querySelector('[data-testid="list-pagination-next"]') as HTMLButtonElement).disabled).toBe(true);
    expect((host.querySelector('[data-testid="list-pagination-previous"]') as HTMLButtonElement).disabled).toBe(false);
  });

  it('reaches every one of 25 purchases once, in the API order, and steps back with Previous', async () => {
    const { fixture, host } = await render(many(25));
    const seen: string[] = [];

    seen.push(...rowTitles(host));
    click(host, 'list-pagination-next');
    fixture.detectChanges();
    seen.push(...rowTitles(host));
    click(host, 'list-pagination-next');
    fixture.detectChanges();
    seen.push(...rowTitles(host));

    expect(seen).toEqual(many(25).map((p) => p.title));
    expect(rangeText(host)).toBe('Showing 21–25 of 25 purchases');

    click(host, 'list-pagination-previous');
    fixture.detectChanges();
    expect(rangeText(host)).toBe('Showing 11–20 of 25 purchases');
  });

  it('returns to page 1 when the page size changes', async () => {
    const { fixture, host } = await render(many(25));
    click(host, 'list-pagination-next');
    fixture.detectChanges();

    chooseSize(host, 20);
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toBe('Page 1 of 2');
    expect(rowTitles(host)).toHaveLength(20);
    expect(rowTitles(host)[0]).toBe('Purchase 1');
  });

  it('reads the list once per load and never again on a page turn or size change', async () => {
    const { fixture, host, getAll } = await render(many(25));

    click(host, 'list-pagination-next');
    fixture.detectChanges();
    chooseSize(host, 5);
    fixture.detectChanges();
    click(host, 'list-pagination-next');
    fixture.detectChanges();

    expect(getAll).toHaveBeenCalledTimes(1);
  });

  it('requests no purchase attachment and creates no object URL on initial load, refresh, pagination or a page-size change, even for rows with an image receipt', async () => {
    // Odd ids have an image, even ids a PDF; 3 and 13 have none at all.
    const list = many(15, (id) => (id === 3 || id === 13 ? { ...withoutDocument(), title: `Purchase ${id}` } : id % 2 ? image(id) : {}));
    const { fixture, host, getAll } = await render(list);

    expect(fileIds()).toEqual([]);

    click(host, 'list-pagination-next');
    fixture.detectChanges();
    chooseSize(host, 5);
    fixture.detectChanges();
    fixture.componentInstance.load();
    fixture.detectChanges();

    expect(fileIds()).toEqual([]);
    expect(URL.createObjectURL).not.toHaveBeenCalled();
    expect(getAll).toHaveBeenCalledTimes(2);
  });

  it('shows an accessible attachment control built from list metadata for both an image and a PDF receipt, with no thumbnail image rendered', async () => {
    const { host } = await render([purchase({ id: 1, title: 'Image receipt', ...image(1) }), purchase({ id: 2, title: 'PDF receipt' })]);

    expect(host.querySelector('[aria-label="Open document for Image receipt"]')).not.toBeNull();
    expect(host.querySelector('[aria-label="Open document for PDF receipt"]')).not.toBeNull();
    expect(host.querySelectorAll('img')).toHaveLength(0);
    expect(fileIds()).toEqual([]);
  });

  it('fetches the file and opens it only once the operator activates the attachment control, and revokes its object URL on teardown', async () => {
    const { fixture, host } = await render([purchase({ id: 1, title: 'Weekly restock', ...image(1) })]);
    const openSpy = jest.spyOn(window, 'open').mockImplementation(() => null);

    (host.querySelector('[aria-label="Open document for Weekly restock"]') as HTMLButtonElement).click();

    expect(fileIds()).toEqual([1]);
    expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
    expect(openSpy).toHaveBeenCalledWith('blob:opened-1', '_blank', 'noopener');

    fixture.destroy();

    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(1);
  });

  it('stays on the page shown after a re-read that keeps it', async () => {
    const { fixture, host, getAll } = await render(many(25));
    click(host, 'list-pagination-next');
    fixture.detectChanges();

    getAll.mockReturnValueOnce(of(many(24)));
    fixture.componentInstance.load();
    fixture.detectChanges();

    expect(rangeText(host)).toBe('Showing 11–20 of 24 purchases');
  });

  it('moves to the new last page when a Delete empties the page shown', async () => {
    const { fixture, host, getAll } = await render(many(11));
    click(host, 'list-pagination-next');
    fixture.detectChanges();
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    getAll.mockReturnValueOnce(of(many(10)));

    fixture.componentInstance.remove(many(11)[10]);
    fixture.detectChanges();

    expect(TestBed.inject(PurchaseService).delete).toHaveBeenCalledWith(11);
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toBe('Page 1 of 1');
    expect(rowTitles(host)).toHaveLength(10);
  });

  it('starts no file request and creates no object URL when the list arrives after the page was left', async () => {
    const late = new Subject<Purchase[]>();
    const { fixture, getAll } = await render([]);
    getAll.mockReturnValueOnce(late.asObservable());
    fixture.componentInstance.load();

    fixture.destroy();
    late.next(many(3, image));
    late.complete();

    expect(late.observed).toBe(false);
    expect(TestBed.inject(PurchaseService).getFile).not.toHaveBeenCalled();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
    expect(fixture.componentInstance.purchases).toEqual([]);
  });

  it('reads nothing when a Delete is answered after the page was left', async () => {
    const lateDelete = new Subject<void>();
    const { fixture, getAll } = await render(many(2, image));
    const service = TestBed.inject(PurchaseService);
    (service.delete as jest.Mock).mockReturnValueOnce(lateDelete.asObservable());
    jest.spyOn(window, 'confirm').mockReturnValue(true);
    (service.getFile as jest.Mock).mockClear();
    (URL.createObjectURL as jest.Mock).mockClear();

    fixture.componentInstance.remove(many(2)[0]);
    fixture.destroy();
    lateDelete.next();

    expect(getAll).toHaveBeenCalledTimes(1);
    expect(service.getFile).not.toHaveBeenCalled();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
