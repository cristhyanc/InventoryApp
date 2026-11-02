import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import {
  SuperAdminMasterProduct,
  SuperAdminMasterProductsService
} from '../../../services/super-admin-master-products.service';
import { MasterProductSizeUnit } from '../../../services/super-admin-price-catalogue-review.service';
import { MasterProductFormComponent } from './master-product-form.component';

function product(overrides: Partial<SuperAdminMasterProduct> = {}): SuperAdminMasterProduct {
  return {
    id: 7,
    brand: 'Coke',
    name: 'Clasic',
    sizeValue: 375,
    sizeUnit: MasterProductSizeUnit.Millilitre,
    createdAtUtc: '2026-10-28T00:00:00Z',
    updatedAtUtc: '2026-10-31T01:02:03Z',
    listingCounts: { proposed: 1, confirmed: 0, needsReview: 0, rejected: 0, total: 1 },
    ...overrides
  };
}

async function render(
  service: Partial<SuperAdminMasterProductsService>,
  editing: SuperAdminMasterProduct | null = null
) {
  await TestBed.configureTestingModule({
    imports: [MasterProductFormComponent],
    providers: [{ provide: SuperAdminMasterProductsService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(MasterProductFormComponent);
  fixture.componentRef.setInput('product', editing);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function testId(host: HTMLElement, id: string): HTMLElement {
  const match = host.querySelector(`[data-testid="${id}"]`);
  expect(match).not.toBeNull();
  return match as HTMLElement;
}

/**
 * The one form that adds a master product and corrects one (issue #1024).
 *
 * Every rule it is held to is the server's, so what is asserted here is what it *sends*, what it
 * *shows* when the server refuses, and the one thing it withholds locally - an incomplete
 * description.
 */
describe('MasterProductFormComponent (issue #1024)', () => {
  describe('adding one', () => {
    it('posts the typed description, trimmed, with the chosen unit', async () => {
      const create = jest.fn(() => of(product({ brand: 'Solo', name: 'Lemon', sizeValue: 600 })));
      const { component } = await render({ create });

      component.brand = '  Solo  ';
      component.name = ' Lemon ';
      component.sizeValue = 600;
      component.sizeUnit = MasterProductSizeUnit.Millilitre;
      component.submit();

      expect(create).toHaveBeenCalledWith({
        brand: 'Solo',
        name: 'Lemon',
        sizeValue: 600,
        sizeUnit: MasterProductSizeUnit.Millilitre
      });
    });

    it('tells the page what happened so the catalogue is re-read', async () => {
      const { component } = await render({
        create: jest.fn(() => of(product({ brand: 'Solo', name: 'Lemon' })))
      });
      const saved = jest.fn();
      component.saved.subscribe(saved);

      component.brand = 'Solo';
      component.name = 'Lemon';
      component.sizeValue = 600;
      component.submit();

      expect(saved).toHaveBeenCalledWith('Solo Lemon was added to the catalogue.');
    });

    it('starts from an empty form and calls itself "New master product"', async () => {
      const { component, host } = await render({ create: jest.fn(() => of(product())) });

      expect(component.editing).toBe(false);
      expect(component.brand).toBe('');
      expect(component.name).toBe('');
      expect(component.sizeValue).toBeNull();
      expect(host.textContent).toContain('New master product');
    });
  });

  describe('correcting one', () => {
    it('starts from the row own values', async () => {
      const { component, host } = await render({ update: jest.fn(() => of(product())) }, product());

      expect(component.editing).toBe(true);
      expect(component.brand).toBe('Coke');
      expect(component.name).toBe('Clasic');
      expect(component.sizeValue).toBe(375);
      expect(host.textContent).toContain('Edit master product');
    });

    /**
     * The decisive thing a correction sends: the `updatedAtUtc` this page read, unchanged. The
     * server compares it with the row's own, so an edit prepared against a page that has moved on is
     * refused rather than applied.
     */
    it('puts the correction with the instant the page read', async () => {
      const update = jest.fn(() => of(product({ brand: 'Coca-Cola', name: 'Classic' })));
      const { component } = await render({ update }, product({ updatedAtUtc: '2026-10-31T01:02:03Z' }));

      component.brand = 'Coca-Cola';
      component.name = 'Classic';
      component.submit();

      expect(update).toHaveBeenCalledWith(7, {
        brand: 'Coca-Cola',
        name: 'Classic',
        sizeValue: 375,
        sizeUnit: MasterProductSizeUnit.Millilitre,
        lastChangedAtUtc: '2026-10-31T01:02:03Z'
      });
    });

    it('tells the page the correction went through', async () => {
      const { component } = await render(
        { update: jest.fn(() => of(product({ brand: 'Coca-Cola', name: 'Classic' }))) },
        product()
      );
      const saved = jest.fn();
      component.saved.subscribe(saved);

      component.submit();

      expect(saved).toHaveBeenCalledWith('Coca-Cola Classic was corrected.');
    });

    /**
     * The size safeguard is *said*, never enforced here: the refusal is the server's, and hiding the
     * size fields would also hide why a correction could not be made.
     */
    it('says the size is fixed while a listing is confirmed, and still submits what was typed', async () => {
      const update = jest.fn(() => of(product()));
      const { component, host } = await render(
        { update },
        product({ listingCounts: { proposed: 0, confirmed: 2, needsReview: 0, rejected: 0, total: 2 } })
      );

      expect(component.hasConfirmedListing).toBe(true);
      expect(testId(host, 'master-product-form-size-locked').textContent).toContain('confirmed');
      expect((host.querySelector('[data-testid="master-product-form-size-value"]') as HTMLInputElement).disabled).toBe(
        false
      );

      component.sizeValue = 600;
      component.submit();

      expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ sizeValue: 600 }));
    });

    it('says nothing about a fixed size when no listing is confirmed', async () => {
      const { host } = await render({ update: jest.fn(() => of(product())) }, product());

      expect(host.querySelector('[data-testid="master-product-form-size-locked"]')).toBeNull();
    });

    /** A different row starts from that row's values rather than the previous one's. */
    it('reloads its values when another product is given', async () => {
      const { component, fixture } = await render({ update: jest.fn(() => of(product())) }, product());

      component.brand = 'half typed';
      fixture.componentRef.setInput('product', product({ id: 9, brand: 'Pepsi', name: 'Max' }));
      fixture.detectChanges();

      expect(component.brand).toBe('Pepsi');
      expect(component.name).toBe('Max');
    });
  });

  describe('refusals', () => {
    /** Every rule is the server's, so what a refusal shows is the server's own sentence. */
    it.each([400, 409])('shows the server own sentence for a %p', async (status) => {
      const refusal = {
        status,
        error: { message: 'Another master product already describes this brand, name and size.' }
      };
      const { component, host, fixture } = await render({ create: jest.fn(() => throwError(() => refusal)) });

      component.brand = 'Solo';
      component.name = 'Lemon';
      component.sizeValue = 600;
      component.submit();
      fixture.detectChanges();

      expect(testId(host, 'master-product-form-error').textContent).toContain(
        'Another master product already describes this brand, name and size.'
      );
      expect(component.submitting).toBe(false);
    });

    it('falls back to its own sentence when a refusal carries none', async () => {
      const { component, fixture, host } = await render(
        { update: jest.fn(() => throwError(() => ({ status: 500 }))) },
        product()
      );

      component.submit();
      fixture.detectChanges();

      expect(testId(host, 'master-product-form-error').textContent).toContain(
        'Unable to correct this master product.'
      );
    });

    /**
     * The one thing withheld locally: a request that cannot describe a product is not one, so an
     * incomplete form is not sent at all.
     */
    it.each([
      ['no brand', { brand: '  ', name: 'Lemon', sizeValue: 600 }],
      ['no name', { brand: 'Solo', name: '', sizeValue: 600 }],
      ['no size', { brand: 'Solo', name: 'Lemon', sizeValue: null }]
    ])('does not submit a form with %s', async (_label, values) => {
      const create = jest.fn(() => of(product()));
      const { component, fixture, host } = await render({ create });

      component.brand = values.brand;
      component.name = values.name;
      component.sizeValue = values.sizeValue;
      component.submit();
      fixture.detectChanges();

      expect(create).not.toHaveBeenCalled();
      expect(testId(host, 'master-product-form-error').textContent).toContain(
        'A master product needs a brand, a name and a size.'
      );
    });
  });

  /**
   * One instance of this form serves both the row being corrected and the next one, and the page
   * leaves New and Edit available while a write is in flight. An answer that arrives after the form
   * has become a *different* form must therefore describe nothing on screen: it may not close that
   * form, clear its submitting state or show the other product's refusal as that row's own. A write
   * already sent is never treated as undone - an accepted one is reported to the page so the
   * catalogue is re-read.
   */
  describe('an answer to a submission the form has moved on from', () => {
    it('reports an accepted correction to the page without touching the row now being corrected', async () => {
      const pending = new Subject<SuperAdminMasterProduct>();
      const { component, fixture, host } = await render(
        { update: jest.fn(() => pending) },
        product({ id: 7, brand: 'Coke', name: 'Clasic' })
      );
      const saved = jest.fn();
      const savedAfterMovingOn = jest.fn();
      component.saved.subscribe(saved);
      component.savedAfterMovingOn.subscribe(savedAfterMovingOn);

      component.brand = 'Coca-Cola';
      component.submit();

      // The page hands the same instance another row while the correction above is still unanswered.
      fixture.componentRef.setInput('product', product({ id: 9, brand: 'Pepsi', name: 'Maxx' }));
      fixture.detectChanges();
      component.name = 'Max';

      pending.next(product({ id: 7, brand: 'Coca-Cola', name: 'Clasic' }));
      pending.complete();
      fixture.detectChanges();

      expect(saved).not.toHaveBeenCalled();
      expect(savedAfterMovingOn).toHaveBeenCalledWith('Coca-Cola Clasic was corrected.');
      expect(component.brand).toBe('Pepsi');
      expect(component.name).toBe('Max');
      expect(component.submitting).toBe(false);
      expect(component.error).toBeNull();
      expect(host.querySelector('[data-testid="master-product-form-error"]')).toBeNull();
    });

    it('says nothing at all when the refused submission is one the form has moved on from', async () => {
      const pending = new Subject<SuperAdminMasterProduct>();
      const { component, fixture, host } = await render(
        { update: jest.fn(() => pending) },
        product({ id: 7, brand: 'Coke', name: 'Clasic' })
      );
      const saved = jest.fn();
      const savedAfterMovingOn = jest.fn();
      component.saved.subscribe(saved);
      component.savedAfterMovingOn.subscribe(savedAfterMovingOn);

      component.submit();

      fixture.componentRef.setInput('product', product({ id: 9, brand: 'Pepsi', name: 'Maxx' }));
      fixture.detectChanges();
      component.name = 'Max';

      pending.error({
        status: 409,
        error: { message: 'Another master product already describes this brand, name and size.' }
      });
      fixture.detectChanges();

      expect(saved).not.toHaveBeenCalled();
      expect(savedAfterMovingOn).not.toHaveBeenCalled();
      expect(component.error).toBeNull();
      expect(host.querySelector('[data-testid="master-product-form-error"]')).toBeNull();
      expect(component.brand).toBe('Pepsi');
      expect(component.name).toBe('Max');
      expect(component.submitting).toBe(false);
    });

    /** A dismissed form is not the form that write was sent from either. */
    it('does not report an accepted write as the dismissed form own outcome', async () => {
      const pending = new Subject<SuperAdminMasterProduct>();
      const { component } = await render({ create: jest.fn(() => pending) });
      const saved = jest.fn();
      const savedAfterMovingOn = jest.fn();
      component.saved.subscribe(saved);
      component.savedAfterMovingOn.subscribe(savedAfterMovingOn);

      component.brand = 'Solo';
      component.name = 'Lemon';
      component.sizeValue = 600;
      component.submit();
      component.cancel();

      expect(component.submitting).toBe(false);

      pending.next(product({ brand: 'Solo', name: 'Lemon' }));
      pending.complete();

      expect(saved).not.toHaveBeenCalled();
      expect(savedAfterMovingOn).toHaveBeenCalledWith('Solo Lemon was added to the catalogue.');
      expect(component.error).toBeNull();
    });

    /** The next submission from the form on screen is answered normally. */
    it('still answers the submission the form on screen did make', async () => {
      const first = new Subject<SuperAdminMasterProduct>();
      const update = jest
        .fn()
        .mockReturnValueOnce(first)
        .mockReturnValueOnce(of(product({ id: 9, brand: 'Pepsi', name: 'Max' })));
      const { component, fixture } = await render({ update }, product({ id: 7, brand: 'Coke' }));
      const saved = jest.fn();
      component.saved.subscribe(saved);

      component.submit();
      fixture.componentRef.setInput('product', product({ id: 9, brand: 'Pepsi', name: 'Maxx' }));
      fixture.detectChanges();

      component.name = 'Max';
      component.submit();
      first.next(product({ id: 7, brand: 'Coke' }));
      first.complete();

      expect(saved).toHaveBeenCalledTimes(1);
      expect(saved).toHaveBeenCalledWith('Pepsi Max was corrected.');
    });
  });

  it('asks the page to close it when it is cancelled, writing nothing', async () => {
    const create = jest.fn(() => of(product()));
    const { component, host } = await render({ create });
    const cancelled = jest.fn();
    component.cancelled.subscribe(cancelled);

    testId(host, 'master-product-form-cancel').click();

    expect(cancelled).toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
  });

  /** Nothing about any business is in the contract, so there is nothing of the kind to render. */
  it('shows nothing about any business', async () => {
    const { host } = await render({ update: jest.fn(() => of(product())) }, product());

    const text = host.textContent ?? '';
    expect(text).not.toMatch(/\bbusinesses?\b/i);
    expect(text).not.toMatch(/tracking/i);
  });
});
