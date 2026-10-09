import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { SupplierGstDefaultsComponent } from './supplier-gst-defaults.component';
import { SupplierService } from '../../../services/supplier.service';
import { GstClassification, SupplierGstDefaults } from '../../../models/models';

const noDefaults = {
  supplierId: 7,
  productLineGstDefault: GstClassification.Unknown,
  deliveryGstDefault: GstClassification.Unknown,
  packageGstDefault: GstClassification.Unknown
};

const defaultsFor = (
  supplierId: number,
  productLineGstDefault: GstClassification,
  deliveryGstDefault: GstClassification,
  packageGstDefault: GstClassification
): SupplierGstDefaults => ({
  supplierId,
  productLineGstDefault,
  deliveryGstDefault,
  packageGstDefault
});

async function render(
  getGstDefaults: jest.Mock = jest.fn(() => of(noDefaults)),
  setGstDefaults: jest.Mock = jest.fn(() => of(undefined))
) {
  await TestBed.configureTestingModule({
    imports: [SupplierGstDefaultsComponent],
    providers: [{ provide: SupplierService, useValue: { getGstDefaults, setGstDefaults } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(SupplierGstDefaultsComponent);
  fixture.componentRef.setInput('supplierId', 7);
  fixture.componentRef.setInput('supplierName', 'Wholesale Co');
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const saveButton = () =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      button.textContent?.trim().startsWith('Save')
    )!;
  const save = () => {
    saveButton().click();
    fixture.detectChanges();
  };
  const openSupplier = (supplierId: number, supplierName: string) => {
    fixture.componentRef.setInput('supplierId', supplierId);
    fixture.componentRef.setInput('supplierName', supplierName);
    fixture.detectChanges();
  };

  return { fixture, host, saveButton, save, openSupplier, getGstDefaults, setGstDefaults };
}

describe('SupplierGstDefaultsComponent', () => {
  it('loads the supplier defaults into three separate pickers', async () => {
    const { fixture, host, getGstDefaults } = await render(
      jest.fn(() =>
        of({
          supplierId: 7,
          productLineGstDefault: GstClassification.GstFree,
          deliveryGstDefault: GstClassification.Taxable,
          packageGstDefault: GstClassification.Unknown
        })
      )
    );

    expect(getGstDefaults).toHaveBeenCalledWith(7);
    expect(fixture.componentInstance.form).toEqual({
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    });
    expect(host.querySelector('#supplier-gst-product-lines')).not.toBeNull();
    expect(host.querySelector('#supplier-gst-delivery')).not.toBeNull();
    expect(host.querySelector('#supplier-gst-package')).not.toBeNull();
    expect(host.textContent).toContain('Wholesale Co');
  });

  /**
   * The charge defaults must be sent as their own values. A delivery charge never inherits the
   * product-line default (parent issue #62), so a save that collapsed the three into one would be
   * wrong even though every value it sent was valid.
   */
  it('saves the three defaults independently', async () => {
    const { fixture, save, setGstDefaults } = await render();

    fixture.componentInstance.form = {
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    };
    fixture.detectChanges();
    save();

    expect(setGstDefaults).toHaveBeenCalledWith(7, {
      productLineGstDefault: GstClassification.GstFree,
      deliveryGstDefault: GstClassification.Taxable,
      packageGstDefault: GstClassification.Unknown
    });
  });

  it('offers no-default, taxable and GST-free for every picker', async () => {
    const { host } = await render();

    const labels = Array.from(host.querySelectorAll<HTMLSelectElement>('select')).map((select) =>
      Array.from(select.querySelectorAll('option')).map((option) => option.textContent?.trim())
    );

    expect(labels).toEqual([
      ['No default', 'Taxable', 'GST-free'],
      ['No default', 'Taxable', 'GST-free'],
      ['No default', 'Taxable', 'GST-free']
    ]);
  });

  it('reports a failed save and does not claim it succeeded', async () => {
    const { host, save } = await render(
      undefined,
      jest.fn(() => throwError(() => new Error('rejected')))
    );

    save();

    expect(host.textContent).toContain('Failed to save the GST defaults.');
    expect(host.textContent).not.toContain('GST defaults saved.');
  });

  it('reports a failed load', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('unavailable'))));

    expect(host.textContent).toContain('Failed to load the GST defaults.');
  });

  /**
   * A failed read leaves the form on its "no default" placeholders, which are a real value the API
   * would store. Saving them would erase whatever defaults the supplier already has, so saving must
   * stay impossible until the stored defaults have actually been read back.
   */
  it('keeps saving disabled after a failed load, so a failed GET cannot lead to a PUT', async () => {
    const { fixture, saveButton, save, setGstDefaults } = await render(
      jest.fn(() => throwError(() => new Error('unavailable')))
    );

    expect(fixture.componentInstance.loaded).toBe(false);
    expect(saveButton().disabled).toBe(true);
    save();
    fixture.componentInstance.save();

    expect(setGstDefaults).not.toHaveBeenCalled();
  });

  it('keeps saving disabled while the defaults are still loading', async () => {
    const { saveButton, fixture, setGstDefaults } = await render(jest.fn(() => new Subject<SupplierGstDefaults>()));

    expect(saveButton().disabled).toBe(true);
    fixture.componentInstance.save();

    expect(setGstDefaults).not.toHaveBeenCalled();
  });

  /**
   * Opening supplier A and then B leaves A's read in flight. If A's slower response populated the
   * form, the next save would write A's defaults onto B. The read for the supplier no longer on
   * screen must be abandoned, whenever it finishes.
   */
  it('ignores a slower read for a supplier that is no longer on screen', async () => {
    const reads = new Map<number, Subject<SupplierGstDefaults>>();
    const getGstDefaults = jest.fn((id: number) => {
      const read = new Subject<SupplierGstDefaults>();
      reads.set(id, read);
      return read;
    });
    const { fixture, host, save, openSupplier, setGstDefaults } = await render(getGstDefaults);

    openSupplier(8, 'Bulk Supplies');
    reads.get(8)!.next(defaultsFor(8, GstClassification.Taxable, GstClassification.Unknown, GstClassification.GstFree));
    fixture.detectChanges();

    // Supplier 7's response arrives out of order, after supplier 8 is already on screen.
    reads.get(7)!.next(defaultsFor(7, GstClassification.GstFree, GstClassification.GstFree, GstClassification.GstFree));
    fixture.detectChanges();

    expect(fixture.componentInstance.form).toEqual({
      productLineGstDefault: GstClassification.Taxable,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.GstFree
    });
    expect(host.textContent).toContain('Bulk Supplies');

    save();

    expect(setGstDefaults).toHaveBeenCalledTimes(1);
    expect(setGstDefaults).toHaveBeenCalledWith(8, {
      productLineGstDefault: GstClassification.Taxable,
      deliveryGstDefault: GstClassification.Unknown,
      packageGstDefault: GstClassification.GstFree
    });
  });

  /**
   * The same ordering problem applies to a save: its outcome belongs to the supplier it was issued
   * for, so it must not be reported on - or re-enable - the form of a supplier opened since.
   */
  it('does not report a save for one supplier on the form of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstDefaults = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openSupplier } = await render(
      jest.fn((id: number) => of(defaultsFor(id, GstClassification.Taxable, GstClassification.Unknown, GstClassification.Unknown))),
      setGstDefaults
    );

    save();
    openSupplier(8, 'Bulk Supplies');

    saves.get(7)!.next();
    saves.get(7)!.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('GST defaults saved.');
    expect(fixture.componentInstance.saved).toBe(false);
  });

  it('does not report a failed save for one supplier on the form of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstDefaults = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openSupplier } = await render(
      jest.fn((id: number) => of(defaultsFor(id, GstClassification.Taxable, GstClassification.Unknown, GstClassification.Unknown))),
      setGstDefaults
    );

    save();
    openSupplier(8, 'Bulk Supplies');

    saves.get(7)!.error(new Error('rejected'));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Failed to save the GST defaults.');
    expect(fixture.componentInstance.error).toBe('');
  });

  /**
   * The copy the acceptance criteria call for: the page has to say that a supplier default applies
   * only where no product rule exists, and that configuring one changes no recorded purchase.
   */
  it('explains that a default applies only where no product rule exists', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('applies only where the purchased product has no GST rule of its own');
    expect(host.textContent).toContain('never inherit the product-line default');
    expect(host.textContent).toContain('does not change the GST classification of any');
  });
});
