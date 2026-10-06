import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { SupplierGstDefaultsComponent } from './supplier-gst-defaults.component';
import { SupplierService } from '../../../services/supplier.service';
import { GstClassification } from '../../../models/models';

const noDefaults = {
  supplierId: 7,
  productLineGstDefault: GstClassification.Unknown,
  deliveryGstDefault: GstClassification.Unknown,
  packageGstDefault: GstClassification.Unknown
};

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
  const save = () => {
    Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent?.trim().startsWith('Save'))!
      .click();
    fixture.detectChanges();
  };

  return { fixture, host, save, getGstDefaults, setGstDefaults };
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
