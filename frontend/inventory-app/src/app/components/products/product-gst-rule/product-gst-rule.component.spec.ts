import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { ProductGstRuleComponent } from './product-gst-rule.component';
import { ProductService } from '../../../services/product.service';
import { GstClassification, ProductGstRule } from '../../../models/models';

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
  const openProduct = (productId: number) => {
    fixture.componentRef.setInput('productId', productId);
    fixture.detectChanges();
  };

  return { fixture, host, select, saveButton, save, openProduct, getGstRule, setGstRule };
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
   * A failed read leaves the picker on its "no rule" placeholder, which is a real value the API
   * would store. Saving it would erase whatever rule the product already has, so saving must stay
   * impossible until the stored rule has actually been read back.
   */
  it('keeps saving disabled after a failed load, so a failed GET cannot lead to a PUT', async () => {
    const { fixture, saveButton, save, setGstRule } = await render(
      jest.fn(() => throwError(() => new Error('unavailable')))
    );

    expect(fixture.componentInstance.loaded).toBe(false);
    expect(saveButton().disabled).toBe(true);
    save();
    fixture.componentInstance.save();

    expect(setGstRule).not.toHaveBeenCalled();
  });

  it('keeps saving disabled while the rule is still loading', async () => {
    const { fixture, saveButton, setGstRule } = await render(jest.fn(() => new Subject<ProductGstRule>()));

    expect(saveButton().disabled).toBe(true);
    fixture.componentInstance.save();

    expect(setGstRule).not.toHaveBeenCalled();
  });

  /**
   * Opening product A and then B leaves A's read in flight. If A's slower response populated the
   * picker, the next save would write A's rule onto B. The read for the product no longer on screen
   * must be abandoned, whenever it finishes.
   */
  it('ignores a slower read for a product that is no longer on screen', async () => {
    const reads = new Map<number, Subject<ProductGstRule>>();
    const getGstRule = jest.fn((id: number) => {
      const read = new Subject<ProductGstRule>();
      reads.set(id, read);
      return read;
    });
    const { fixture, save, openProduct, setGstRule } = await render(getGstRule);

    openProduct(43);
    reads.get(43)!.next({ productId: 43, gstRule: GstClassification.Taxable });
    fixture.detectChanges();

    // Product 42's response arrives out of order, after product 43 is already on screen.
    reads.get(42)!.next({ productId: 42, gstRule: GstClassification.GstFree });
    fixture.detectChanges();

    expect(fixture.componentInstance.rule).toBe(GstClassification.Taxable);

    save();

    expect(setGstRule).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledWith(43, GstClassification.Taxable);
  });

  /**
   * The same ordering problem applies to a save: its outcome belongs to the product it was issued
   * for, so it must not be reported on - or re-enable - the picker of a product opened since.
   */
  it('does not report a save for one product on the picker of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstRule = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openProduct } = await render(
      jest.fn((id: number) => of({ productId: id, gstRule: GstClassification.Taxable })),
      setGstRule
    );

    save();
    openProduct(43);

    saves.get(42)!.next();
    saves.get(42)!.complete();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('GST rule saved.');
    expect(fixture.componentInstance.saved).toBe(false);
  });

  it('does not report a failed save for one product on the picker of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstRule = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, host, save, openProduct } = await render(
      jest.fn((id: number) => of({ productId: id, gstRule: GstClassification.Taxable })),
      setGstRule
    );

    save();
    openProduct(43);

    saves.get(42)!.error(new Error('rejected'));
    fixture.detectChanges();

    expect(host.textContent).not.toContain('Failed to save the GST rule.');
    expect(fixture.componentInstance.error).toBe('');
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
