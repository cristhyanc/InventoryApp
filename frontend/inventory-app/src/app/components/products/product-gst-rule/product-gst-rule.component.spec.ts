import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ProductGstRuleComponent } from './product-gst-rule.component';
import { ProductService } from '../../../services/product.service';
import { GstClassification } from '../../../models/models';

async function render(
  getGstRule: jest.Mock = jest.fn(() => of({ productId: 42, gstRule: GstClassification.Unknown })),
  setGstRule: jest.Mock = jest.fn(() => of(undefined))
) {
  await TestBed.configureTestingModule({
    imports: [ProductGstRuleComponent],
    providers: [{ provide: ProductService, useValue: { getGstRule, setGstRule } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductGstRuleComponent);
  fixture.componentRef.setInput('productId', 42);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const select = () => host.querySelector<HTMLSelectElement>('#product-gst-rule')!;
  const saveButton = () =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      button.textContent?.trim().startsWith('Save')
    )!;
  const save = () => {
    saveButton().click();
    fixture.detectChanges();
  };

  return { fixture, host, select, saveButton, save, getGstRule, setGstRule };
}

describe('ProductGstRuleComponent', () => {
  it('loads the product rule and offers no-rule, taxable and GST-free', async () => {
    const { fixture, host, getGstRule } = await render(
      jest.fn(() => of({ productId: 42, gstRule: GstClassification.GstFree }))
    );

    expect(getGstRule).toHaveBeenCalledWith(42);
    expect(fixture.componentInstance.rule).toBe(GstClassification.GstFree);
    expect(Array.from(host.querySelectorAll('option')).map((option) => option.textContent?.trim())).toEqual([
      'No rule',
      'Taxable',
      'GST-free'
    ]);
  });

  it('saves the selected rule', async () => {
    const { fixture, host, save, setGstRule } = await render();

    fixture.componentInstance.rule = GstClassification.Taxable;
    fixture.detectChanges();
    save();

    expect(setGstRule).toHaveBeenCalledWith(42, GstClassification.Taxable);
    expect(host.textContent).toContain('GST rule saved.');
  });

  /**
   * "No rule" is a real value a person can choose, not an absence: saving it must send `Unknown`
   * so the rule is cleared, which is not the same as saving GST-free.
   */
  it('saves no rule as the unknown classification rather than as GST-free', async () => {
    const { fixture, save, setGstRule } = await render(
      jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable }))
    );

    fixture.componentInstance.rule = GstClassification.Unknown;
    fixture.detectChanges();
    save();

    expect(setGstRule).toHaveBeenCalledWith(42, GstClassification.Unknown);
  });

  it('reports a failed save and does not claim it succeeded', async () => {
    const { host, save } = await render(
      undefined,
      jest.fn(() => throwError(() => new Error('rejected')))
    );

    save();

    expect(host.textContent).toContain('Failed to save the GST rule.');
    expect(host.textContent).not.toContain('GST rule saved.');
  });

  it('reports a failed load', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('unavailable'))));

    expect(host.textContent).toContain('Failed to load the GST rule.');
  });

  /**
   * The copy the acceptance criteria call for: the page has to say that a product rule wins over a
   * supplier default, and that configuring one changes no purchase already recorded.
   */
  it('explains the precedence and that recorded purchases are unaffected', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('takes precedence over any supplier default');
    expect(host.textContent).toContain('does not change the GST classification of any');
  });
});
