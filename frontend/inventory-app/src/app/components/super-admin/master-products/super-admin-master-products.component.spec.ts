import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';
import {
  MASTER_PRODUCT_PAGE_SIZE,
  SuperAdminMasterProduct,
  SuperAdminMasterProductDetail,
  SuperAdminMasterProductPage,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import { MasterProductSizeUnit, SupplierListingMatchStatus } from '../../../services/super-admin-price-catalogue-review.service';
import { MasterProductFormComponent } from './master-product-form.component';
import { SuperAdminMasterProductsComponent } from './super-admin-master-products.component';

function product(overrides: Partial<SuperAdminMasterProduct> = {}): SuperAdminMasterProduct {
  return {
    id: 1,
    brand: 'Coca-Cola',
    name: 'Classic',
    sizeValue: 375,
    sizeUnit: MasterProductSizeUnit.Millilitre,
    createdAtUtc: '2026-10-28T00:00:00Z',
    updatedAtUtc: '2026-10-29T00:00:00Z',
    listingCounts: { proposed: 1, confirmed: 2, needsReview: 0, rejected: 0, total: 3 },
    ...overrides
  };
}

function page(overrides: Partial<SuperAdminMasterProductPage> = {}): SuperAdminMasterProductPage {
  return {
    products: [product()],
    totalCount: 1,
    page: 1,
    pageSize: MASTER_PRODUCT_PAGE_SIZE,
    hasMore: false,
    ...overrides
  };
}

const detail: SuperAdminMasterProductDetail = {
  product: product(),
  listings: [
    {
      id: 11,
      supplierName: 'Alpha Grocer',
      supplierProductId: 'SKU-COLA-30',
      title: 'Alpha Grocer Coca-Cola Classic 375mL 30 pack',
      sizeValue: 375,
      sizeUnit: MasterProductSizeUnit.Millilitre,
      unitsPerPack: 30,
      matchStatus: SupplierListingMatchStatus.Confirmed,
      url: 'https://alpha.example/cola',
      lastCheckedAtUtc: '2026-10-29T00:00:00Z',
      latestPrice: null
    }
  ]
};

async function render(service: Partial<SuperAdminMasterProductsService>) {
  await TestBed.configureTestingModule({
    imports: [SuperAdminMasterProductsComponent],
    providers: [{ provide: SuperAdminMasterProductsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SuperAdminMasterProductsComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement {
  const match = host.querySelector(`[data-testid="${id}"]`);
  expect(match).not.toBeNull();
  return match as HTMLElement;
}

describe('SuperAdminMasterProductsComponent (issues #1023 and #1024)', () => {
  it('reads the first page on arrival and lists each product with its listing counts', async () => {
    const list = jest.fn(() =>
      of(
        page({
          products: [
            product({ id: 1, brand: 'Coca-Cola', name: 'Classic' }),
            product({
              id: 2,
              brand: 'Pepsi',
              name: 'Max',
              listingCounts: { proposed: 0, confirmed: 0, needsReview: 0, rejected: 0, total: 0 }
            })
          ],
          totalCount: 2
        })
      )
    );

    const { host } = await render({ list });

    expect(list).toHaveBeenCalledWith({ search: '', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
    expect(host.textContent).toContain('Coca-Cola');
    expect(host.textContent).toContain('Pepsi');
    expect(host.textContent).toContain('375 ml');
    expect(testId(host, 'master-product-page-summary').textContent).toContain('Showing 1–2 of 2');
  });

  it('shows the loading state while the catalogue is still being read', async () => {
    // A read that has not answered yet, which is what the loading state is for.
    const { host, fixture } = await render({ list: jest.fn(() => new Subject<SuperAdminMasterProductPage>()) });

    expect(host.textContent).toContain('Loading master products…');
    expect(host.querySelector('tbody')).toBeNull();

    fixture.destroy();
  });

  it('shows an actionable error state when the catalogue cannot be read, and retries on request', async () => {
    const list = jest.fn(() => throwError(() => new Error('nope')));
    const { host, component } = await render({ list });

    expect(testId(host, 'master-product-error').textContent).toContain('Unable to load master products.');
    expect(component.products).toEqual([]);

    component.load();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('shows an empty state, and says what was searched for when a search found nothing', async () => {
    const list = jest.fn(() => of(page({ products: [], totalCount: 0 })));
    const { host, fixture, component } = await render({ list });

    expect(testId(host, 'master-product-empty').textContent).toContain('No master products yet.');

    component.searchText = 'rhubarb';
    component.search();
    fixture.detectChanges();

    expect(testId(host, 'master-product-empty').textContent).toContain('No master product matches “rhubarb”');
  });

  /**
   * The search is the server's: the page sends the text and re-reads, rather than filtering the one
   * page it happens to be holding - which would silently hide the matches on every other page.
   */
  it('searches on the server and starts again at the first page', async () => {
    const list = jest.fn(() => of(page({ page: 1, totalCount: 1, hasMore: false })));
    const { component } = await render({ list });

    component.page = 3;
    component.searchText = '  cola  ';
    component.search();

    expect(list).toHaveBeenLastCalledWith({ search: 'cola', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
    expect(component.page).toBe(1);
  });

  it('clears a search back to the whole catalogue', async () => {
    const list = jest.fn(() => of(page()));
    const { component } = await render({ list });

    component.searchText = 'cola';
    component.search();
    component.clearSearch();

    expect(component.searchText).toBe('');
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 1, pageSize: MASTER_PRODUCT_PAGE_SIZE });
  });

  it('pages forward and back, and offers neither beyond the ends', async () => {
    const list = jest.fn(() => of(page({ page: 1, pageSize: 1, totalCount: 3, hasMore: true })));
    const { host, component, fixture } = await render({ list });

    // Previous is unavailable on the first page; Next is offered while the server says more remain.
    expect((testId(host, 'master-product-previous') as HTMLButtonElement).disabled).toBe(true);
    expect((testId(host, 'master-product-next') as HTMLButtonElement).disabled).toBe(false);

    (testId(host, 'master-product-next') as HTMLButtonElement).click();
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 2, pageSize: 1 });

    component.page = 2;
    component.hasMore = false;
    fixture.detectChanges();
    expect((testId(host, 'master-product-next') as HTMLButtonElement).disabled).toBe(true);
    expect((testId(host, 'master-product-previous') as HTMLButtonElement).disabled).toBe(false);

    (testId(host, 'master-product-previous') as HTMLButtonElement).click();
    expect(list).toHaveBeenLastCalledWith({ search: '', page: 1, pageSize: 1 });
  });

  /**
   * The server bounds the page it serves and says which one it served; the page displays that answer
   * rather than what it asked for, so the "Showing …" line is never a guess.
   */
  it('displays the page and size the server says it served', async () => {
    const list = jest.fn(() => of(page({ page: 2, pageSize: 200, totalCount: 500, hasMore: true })));
    const { component, host } = await render({ list });

    expect(component.page).toBe(2);
    expect(component.pageSize).toBe(200);
    expect(testId(host, 'master-product-page-summary').textContent).toContain('Showing 201–201 of 500');
  });

  it('opens and closes one product’s supplier listings', async () => {
    const list = jest.fn(() => of(page()));
    const get = jest.fn(() => of(detail));
    const { host, fixture, component } = await render({ list, get });

    (testId(host, 'master-product-listings-1') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.selectedProductId).toBe(1);
    expect(get).toHaveBeenCalledWith(1);
    expect(testId(host, 'master-product-detail').textContent).toContain('Alpha Grocer');

    (testId(host, 'master-product-detail-close') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component.selectedProductId).toBeNull();
    expect(host.querySelector('[data-testid="master-product-detail"]')).toBeNull();
  });

  it('closes an open product when the page or the search changes, so the panel is never about another row', async () => {
    const list = jest.fn(() => of(page({ hasMore: true })));
    const { component } = await render({ list, get: jest.fn(() => of(detail)) });

    component.toggleListings(product());
    component.nextPage();
    expect(component.selectedProductId).toBeNull();

    component.toggleListings(product());
    component.search();
    expect(component.selectedProductId).toBeNull();
  });

  /**
   * The search box stays usable while a read is outstanding, so two reads can be in flight and the
   * slower one can be the older one. Only the newest may describe the page: a "pepsi" search box
   * above the "cola" rows, the "cola" total and the "cola" page number would be a lie about what is
   * on screen.
   */
  it('lets only the newest search describe the page, whichever read answers last', async () => {
    const firstRead = new Subject<SuperAdminMasterProductPage>();
    const secondRead = new Subject<SuperAdminMasterProductPage>();
    const list = jest.fn().mockReturnValueOnce(firstRead).mockReturnValueOnce(secondRead);
    const { component, fixture } = await render({ list });

    component.searchText = 'pepsi';
    component.search();

    secondRead.next(page({ products: [product({ id: 2, brand: 'Pepsi', name: 'Max' })], totalCount: 1, page: 1, hasMore: false }));
    // The first read answers afterwards, with another search's page, total and position.
    firstRead.next(page({ products: [product({ id: 1, brand: 'Coca-Cola' })], totalCount: 40, page: 3, hasMore: true }));
    fixture.detectChanges();

    expect(component.appliedSearch).toBe('pepsi');
    expect(component.products.map((match) => match.id)).toEqual([2]);
    expect(component.totalCount).toBe(1);
    expect(component.page).toBe(1);
    expect(component.hasMore).toBe(false);
  });

  /** An older failure is just as stale as an older success: it must not overwrite a newer answer. */
  it('keeps the newest search on screen when an older read fails afterwards', async () => {
    const firstRead = new Subject<SuperAdminMasterProductPage>();
    const secondRead = new Subject<SuperAdminMasterProductPage>();
    const list = jest.fn().mockReturnValueOnce(firstRead).mockReturnValueOnce(secondRead);
    const { component, fixture, host } = await render({ list });

    component.searchText = 'pepsi';
    component.search();

    secondRead.next(page({ products: [product({ id: 2, brand: 'Pepsi', name: 'Max' })], totalCount: 1 }));
    firstRead.error(new Error('the abandoned search failed'));
    fixture.detectChanges();

    expect(component.loadError).toBe(false);
    expect(component.loading).toBe(false);
    expect(component.products.map((match) => match.id)).toEqual([2]);
    expect(host.querySelector('[data-testid="master-product-error"]')).toBeNull();
  });

  /** A paging step is the same race: the page shown must be the page last asked for. */
  it('lets only the newest page step describe the page, whichever read answers last', async () => {
    const firstRead = new Subject<SuperAdminMasterProductPage>();
    const secondRead = new Subject<SuperAdminMasterProductPage>();
    const thirdRead = new Subject<SuperAdminMasterProductPage>();
    const list = jest.fn().mockReturnValueOnce(firstRead).mockReturnValueOnce(secondRead).mockReturnValueOnce(thirdRead);
    const { component } = await render({ list });

    firstRead.next(page({ page: 1, pageSize: 1, totalCount: 3, hasMore: true }));
    component.nextPage();
    component.nextPage();

    thirdRead.next(page({ products: [product({ id: 3 })], page: 3, pageSize: 1, totalCount: 3, hasMore: false }));
    secondRead.next(page({ products: [product({ id: 2 })], page: 2, pageSize: 1, totalCount: 3, hasMore: true }));

    expect(component.page).toBe(3);
    expect(component.hasMore).toBe(false);
    expect(component.products.map((match) => match.id)).toEqual([3]);
  });

  /** A failed read must not end the page's read stream: the next search has to be answered. */
  it('reads again after a failed read instead of going silent', async () => {
    const list = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new Error('nope')))
      .mockReturnValueOnce(of(page({ products: [product({ id: 5, brand: 'Pepsi' })], totalCount: 1 })));
    const { component, fixture } = await render({ list });

    expect(component.loadError).toBe(true);

    component.searchText = 'pepsi';
    component.search();
    fixture.detectChanges();

    expect(component.loadError).toBe(false);
    expect(component.products.map((match) => match.id)).toEqual([5]);
  });

  it('abandons the read still in flight when it is destroyed', async () => {
    const pending = new Subject<SuperAdminMasterProductPage>();
    const { fixture } = await render({ list: jest.fn(() => pending) });

    expect(pending.observed).toBe(true);

    fixture.destroy();

    expect(pending.observed).toBe(false);
  });

  /**
   * Deleting a master product is explicitly out of issue #1024's scope and the API has no such
   * route, so the page offers no control that could ask for one. The create and edit controls
   * *do* exist now, and are covered in their own describe below.
   */
  it('offers no delete control', async () => {
    const { host } = await render({ list: jest.fn(() => of(page())) });

    const labels = Array.from(host.querySelectorAll('button')).map((button) => button.textContent?.trim() ?? '');
    expect(labels).not.toContain('Delete');
    expect(labels).not.toContain('Remove');
    expect(host.querySelector('[role="dialog"]')).toBeNull();
  });

  /**
   * Nothing about any business can be displayed, because the API publishes none: the rendered page
   * must not invent a tracking count or name a business anywhere.
   */
  it('displays nothing about any business', async () => {
    const { host } = await render({ list: jest.fn(() => of(page())) });

    const text = host.textContent ?? '';
    expect(text).not.toMatch(/\bbusinesses?\b/i);
    expect(text).not.toMatch(/tracking/i);
  });

  /**
   * Issue #1024's own criteria on the page: the two ways in to the form, and the fact that an
   * accepted write re-reads the catalogue, so what is on screen is what is stored.
   */
  describe('managing the catalogue (issue #1024)', () => {
    it('opens the form with no product for a new master product', async () => {
      const { component, host, fixture } = await render({ list: jest.fn(() => of(page())) });

      testId(host, 'master-product-new').click();
      fixture.detectChanges();

      expect(component.formOpen).toBe(true);
      expect(component.editing).toBeNull();
      expect(testId(host, 'master-product-form').textContent).toContain('New master product');
    });

    /**
     * Edit hands the form the whole product, not its id: the correction has to present the
     * `updatedAtUtc` this page read, which is how the server refuses a stale edit.
     */
    it('opens the form with the row being corrected', async () => {
      const row = product({ id: 7, brand: 'Coke', name: 'Clasic', updatedAtUtc: '2026-10-31T01:02:03Z' });
      const { component, host, fixture } = await render({
        list: jest.fn(() => of(page({ products: [row] })))
      });

      testId(host, 'master-product-edit-7').click();
      fixture.detectChanges();

      expect(component.formOpen).toBe(true);
      expect(component.editing).toBe(row);
      expect(testId(host, 'master-product-form').textContent).toContain('Edit master product');
    });

    it('re-reads the catalogue after an accepted write and says what happened', async () => {
      const list = jest
        .fn()
        .mockReturnValueOnce(of(page({ products: [product({ id: 7, brand: 'Coke' })] })))
        .mockReturnValueOnce(of(page({ products: [product({ id: 7, brand: 'Coca-Cola' })] })));
      const { component, host, fixture } = await render({ list });

      component.savedChanges('Coca-Cola Classic was corrected.');
      fixture.detectChanges();

      expect(list).toHaveBeenCalledTimes(2);
      expect(component.formOpen).toBe(false);
      expect(component.editing).toBeNull();
      expect(component.selectedProductId).toBeNull();
      expect(component.products.map((match) => match.brand)).toEqual(['Coca-Cola']);
      expect(testId(host, 'master-product-saved').textContent).toContain('was corrected');
    });

    it('closes the form on cancel without re-reading anything', async () => {
      const list = jest.fn(() => of(page()));
      const { component, fixture } = await render({ list });

      component.startCreating();
      component.closeForm();
      fixture.detectChanges();

      expect(component.formOpen).toBe(false);
      expect(list).toHaveBeenCalledTimes(1);
    });

    /**
     * A search or a page step replaces the rows the form is about, so a half-typed correction
     * prepared against a row nobody is looking at any more is not kept.
     */
    it('closes the form when the search or the page changes', async () => {
      // The server reports page 2 of more, so stepping in either direction really happens: the page
      // displays the slice the server says it served, never the one that was asked for.
      const { component } = await render({ list: jest.fn(() => of(page({ page: 2, hasMore: true }))) });

      component.startEditing(product({ id: 7 }));
      component.search();
      expect(component.formOpen).toBe(false);

      component.startEditing(product({ id: 7 }));
      component.nextPage();
      expect(component.formOpen).toBe(false);

      component.startEditing(product({ id: 7 }));
      component.previousPage();
      expect(component.formOpen).toBe(false);
    });

    /**
     * The page keeps New and every row's Edit available while a write is in flight, and it serves
     * both rows with **one** form instance. The reported defect is what that allows: correct row A,
     * open row B before A's answer arrives, type into B, and A's answer would close B's form and
     * throw away what was typed - or show A's refusal as B's own.
     *
     * The write itself is never treated as undone. An accepted one is announced and the catalogue is
     * re-read; the form on screen is simply left alone.
     */
    describe('a write that answers after the form has moved on', () => {
      function openForm(fixture: ComponentFixture<SuperAdminMasterProductsComponent>) {
        const form = fixture.debugElement.query(By.directive(MasterProductFormComponent));
        expect(form).not.toBeNull();
        return form.componentInstance as MasterProductFormComponent;
      }

      const rowA = product({ id: 7, brand: 'Coke', name: 'Clasic', updatedAtUtc: '2026-10-31T01:02:03Z' });
      const rowB = product({ id: 9, brand: 'Pepsi', name: 'Maxx', updatedAtUtc: '2026-10-31T04:05:06Z' });

      it('keeps the row being corrected, and still says what the accepted write did', async () => {
        const pending = new Subject<SuperAdminMasterProduct>();
        const list = jest.fn(() => of(page({ products: [rowA, rowB], totalCount: 2 })));
        const { component, host, fixture } = await render({ list, update: jest.fn(() => pending) });

        testId(host, 'master-product-edit-7').click();
        fixture.detectChanges();
        const form = openForm(fixture);
        form.brand = 'Coca-Cola';
        form.submit();

        testId(host, 'master-product-edit-9').click();
        fixture.detectChanges();
        // The same instance: it is handed the other row rather than being built again, which is why
        // an answer has to know which form it belongs to.
        expect(openForm(fixture)).toBe(form);
        form.name = 'Max';

        pending.next(product({ id: 7, brand: 'Coca-Cola', name: 'Clasic' }));
        pending.complete();
        fixture.detectChanges();

        expect(component.formOpen).toBe(true);
        expect(component.editing).toBe(rowB);
        expect(form.brand).toBe('Pepsi');
        expect(form.name).toBe('Max');
        expect(form.submitting).toBe(false);
        expect(host.querySelector('[data-testid="master-product-form-error"]')).toBeNull();
        expect(testId(host, 'master-product-form').textContent).toContain('Edit master product');
        // The correction did happen, so it is reported and the catalogue is read again.
        expect(list).toHaveBeenCalledTimes(2);
        expect(testId(host, 'master-product-saved').textContent).toContain('Coca-Cola Clasic was corrected.');
      });

      it('does not show an earlier refusal on the row being corrected, and re-reads nothing', async () => {
        const pending = new Subject<SuperAdminMasterProduct>();
        const list = jest.fn(() => of(page({ products: [rowA, rowB], totalCount: 2 })));
        const { component, host, fixture } = await render({ list, update: jest.fn(() => pending) });

        testId(host, 'master-product-edit-7').click();
        fixture.detectChanges();
        const form = openForm(fixture);
        form.submit();

        testId(host, 'master-product-edit-9').click();
        fixture.detectChanges();
        form.name = 'Max';

        pending.error({
          status: 409,
          error: { message: 'This master product has been changed since this page was read.' }
        });
        fixture.detectChanges();

        expect(component.formOpen).toBe(true);
        expect(component.editing).toBe(rowB);
        expect(form.brand).toBe('Pepsi');
        expect(form.name).toBe('Max');
        expect(form.submitting).toBe(false);
        expect(host.querySelector('[data-testid="master-product-form-error"]')).toBeNull();
        // A refusal wrote nothing, so there is nothing to announce and nothing to re-read.
        expect(list).toHaveBeenCalledTimes(1);
        expect(host.querySelector('[data-testid="master-product-saved"]')).toBeNull();
      });

      /**
       * Dismissing the form while a write is in flight and opening another row: the reopened form is
       * that row's, with none of the dismissed submission's state. The write itself is not announced,
       * because the form that sent it is gone - the catalogue is read again on the next search, page
       * step or accepted write.
       */
      it('reopens a clean form for another row after a write was dismissed mid-flight', async () => {
        const pending = new Subject<SuperAdminMasterProduct>();
        const list = jest.fn(() => of(page({ products: [rowA, rowB], totalCount: 2 })));
        const { component, host, fixture } = await render({ list, update: jest.fn(() => pending) });

        testId(host, 'master-product-edit-7').click();
        fixture.detectChanges();
        const dismissed = openForm(fixture);
        dismissed.brand = 'Coca-Cola';
        dismissed.submit();

        testId(host, 'master-product-form-cancel').click();
        fixture.detectChanges();
        expect(component.formOpen).toBe(false);

        testId(host, 'master-product-edit-9').click();
        fixture.detectChanges();
        const reopened = openForm(fixture);
        reopened.name = 'Max';

        pending.next(product({ id: 7, brand: 'Coca-Cola', name: 'Clasic' }));
        pending.complete();
        fixture.detectChanges();

        expect(component.formOpen).toBe(true);
        expect(component.editing).toBe(rowB);
        expect(reopened.brand).toBe('Pepsi');
        expect(reopened.name).toBe('Max');
        expect(reopened.submitting).toBe(false);
        expect(reopened.error).toBeNull();
        expect(host.querySelector('[data-testid="master-product-form-error"]')).toBeNull();
      });
    });
  });
});
