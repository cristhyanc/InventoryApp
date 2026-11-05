import { TestBed } from '@angular/core/testing';
import { Observable, of, Subject, throwError } from 'rxjs';
import { ProductGstRuleComponent } from './product-gst-rule.component';
import { ProductService } from '../../../services/product.service';
import { GstClassification, ProductGstRule } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';

async function render(
  getGstRule: jest.Mock = jest.fn(() => of({ productId: 42, gstRule: GstClassification.Unknown })),
  setGstRule: jest.Mock = jest.fn(() => of(undefined)),
  sessionActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [ProductGstRuleComponent],
    providers: [
      { provide: ProductService, useValue: { getGstRule, setGstRule } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductGstRuleComponent);
  fixture.componentRef.setInput('productId', 42);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const select = () => host.querySelector<HTMLSelectElement>('#product-gst-rule')!;
  const choose = (label: string) => {
    const picker = select();
    picker.selectedIndex = Array.from(picker.options).findIndex((option) => option.textContent?.trim() === label);
    picker.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };
  /**
   * What the page's Save changes does (issue #1111): asks the panel to write its rule, and records
   * how that write ended.
   */
  const persist = () => {
    const outcome = { completed: false, error: null as unknown };
    fixture.componentInstance.persist().subscribe({
      complete: () => (outcome.completed = true),
      error: (error: unknown) => (outcome.error = error)
    });
    fixture.detectChanges();
    return outcome;
  };
  const openProduct = (productId: number) => {
    fixture.componentRef.setInput('productId', productId);
    fixture.detectChanges();
  };

  return { fixture, host, select, choose, persist, openProduct, getGstRule, setGstRule };
}

/**
 * The same panel on New Product, where the product does not exist yet (issue #829): it chooses the
 * rule and reports it, and `ProductCreateGstRuleComponent` - which composes it there - writes it
 * once the product has an id.
 */
async function renderDeferred(
  getGstRule: jest.Mock = jest.fn(),
  setGstRule: jest.Mock = jest.fn(),
  sessionActive: Observable<boolean> = of(false)
) {
  await TestBed.configureTestingModule({
    imports: [ProductGstRuleComponent],
    providers: [
      { provide: ProductService, useValue: { getGstRule, setGstRule } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductGstRuleComponent);
  fixture.componentRef.setInput('deferred', true);
  const reported: GstClassification[] = [];
  fixture.componentInstance.ruleChange.subscribe((rule) => reported.push(rule));
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const select = () => host.querySelector<HTMLSelectElement>('#product-gst-rule')!;
  const choose = (label: string) => {
    const picker = select();
    picker.selectedIndex = Array.from(picker.options).findIndex((option) => option.textContent?.trim() === label);
    picker.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  return { fixture, host, select, choose, reported, getGstRule, setGstRule };
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

  /**
   * Issue #1111: the panel has no Save button of its own. The page's one Save changes asks it
   * whether the rule changed and, when it did, to write it.
   */
  it('offers no Save button of its own on the edit page', async () => {
    const { host } = await render(jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable })));

    expect(host.querySelectorAll('button')).toHaveLength(0);
  });

  it('is unchanged until a different rule is chosen, and changed back is unchanged again', async () => {
    const { fixture, choose } = await render(jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable })));

    expect(fixture.componentInstance.dirty).toBe(false);
    choose('GST-free');
    expect(fixture.componentInstance.dirty).toBe(true);
    choose('Taxable');
    expect(fixture.componentInstance.dirty).toBe(false);
  });

  it('writes the chosen rule when asked, and is unchanged once it is stored', async () => {
    const { fixture, choose, persist, setGstRule } = await render();

    choose('Taxable');
    const outcome = persist();

    expect(setGstRule).toHaveBeenCalledWith(42, GstClassification.Taxable);
    expect(outcome.completed).toBe(true);
    expect(fixture.componentInstance.dirty).toBe(false);
    expect(fixture.componentInstance.storedRule).toBe(GstClassification.Taxable);
  });

  it('writes nothing for an unchanged rule, and completes at once', async () => {
    const { persist, setGstRule } = await render(jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable })));

    expect(persist().completed).toBe(true);
    expect(setGstRule).not.toHaveBeenCalled();
  });

  /**
   * "No rule" is a real value a person can choose, not an absence: saving it must send `Unknown`
   * so the rule is cleared, which is not the same as saving GST-free.
   */
  it('saves no rule as the unknown classification rather than as GST-free', async () => {
    const { choose, persist, setGstRule } = await render(
      jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable }))
    );

    choose('No rule');
    persist();

    expect(setGstRule).toHaveBeenCalledWith(42, GstClassification.Unknown);
  });

  it('hands a failed write to the page and keeps the choice, so a second Save retries it', async () => {
    const { fixture, choose, persist } = await render(undefined, jest.fn(() => throwError(() => new Error('rejected'))));

    choose('Taxable');
    const outcome = persist();

    expect(outcome.error).toBeInstanceOf(Error);
    expect(outcome.completed).toBe(false);
    expect(fixture.componentInstance.rule).toBe(GstClassification.Taxable);
    expect(fixture.componentInstance.dirty).toBe(true);
  });

  it('reports a failed load', async () => {
    const { host } = await render(jest.fn(() => throwError(() => new Error('unavailable'))));

    expect(host.textContent).toContain('Failed to load the GST rule');
  });

  /**
   * A failed read leaves the picker on its "no rule" placeholder, which is a real value the API
   * would store. Writing it would erase whatever rule the product already has, so the picker stays
   * unusable and the rule is never counted as changed until the stored rule has been read back.
   */
  it('never writes after a failed load, so a failed GET cannot lead to a PUT', async () => {
    const { fixture, select, persist, setGstRule } = await render(
      jest.fn(() => throwError(() => new Error('unavailable')))
    );

    expect(fixture.componentInstance.loaded).toBe(false);
    expect(select().disabled).toBe(true);
    fixture.componentInstance.rule = GstClassification.Taxable;
    expect(fixture.componentInstance.dirty).toBe(false);
    persist();

    expect(setGstRule).not.toHaveBeenCalled();
  });

  it('keeps the picker unusable while the rule is still loading', async () => {
    const { select, persist, setGstRule } = await render(jest.fn(() => new Subject<ProductGstRule>()));

    expect(select().disabled).toBe(true);
    persist();

    expect(setGstRule).not.toHaveBeenCalled();
  });

  it('holds the picker still while the page is saving', async () => {
    const { fixture, select } = await render();

    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(select().disabled).toBe(true);
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
    const { fixture, persist, openProduct, setGstRule } = await render(getGstRule);

    openProduct(43);
    reads.get(43)!.next({ productId: 43, gstRule: GstClassification.Taxable });
    fixture.detectChanges();

    // Product 42's response arrives out of order, after product 43 is already on screen.
    reads.get(42)!.next({ productId: 42, gstRule: GstClassification.GstFree });
    fixture.detectChanges();

    expect(fixture.componentInstance.rule).toBe(GstClassification.Taxable);

    fixture.componentInstance.rule = GstClassification.GstFree;
    persist();

    expect(setGstRule).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledWith(43, GstClassification.GstFree);
  });

  /**
   * The same ordering problem applies to a write: its outcome belongs to the product it was issued
   * for, so it must not mark the picker of a product opened since as stored.
   */
  it('does not record a write for one product on the picker of another', async () => {
    const saves = new Map<number, Subject<void>>();
    const setGstRule = jest.fn((id: number) => {
      const pending = new Subject<void>();
      saves.set(id, pending);
      return pending;
    });
    const { fixture, persist, openProduct } = await render(
      jest.fn((id: number) => of({ productId: id, gstRule: GstClassification.Taxable })),
      setGstRule
    );

    fixture.componentInstance.rule = GstClassification.GstFree;
    persist();
    openProduct(43);

    saves.get(42)!.next();
    saves.get(42)!.complete();
    fixture.detectChanges();

    expect(fixture.componentInstance.storedRule).toBe(GstClassification.Taxable);
  });

  /**
   * The copy issue #1111 calls for: the rule applies to future purchases, recorded purchases keep
   * theirs, "No rule" is not GST-free - and a product rule still wins over a supplier default.
   */
  it('explains future purchases, recorded purchases, "No rule" and the precedence', async () => {
    const { host } = await render();

    expect(host.textContent).toContain('Applies to future purchases. Existing purchases keep their recorded GST treatment.');
    expect(host.textContent).toContain('"No rule" means unclassified, not GST-free.');
    expect(host.textContent).toContain('takes precedence over any supplier default');
  });

  /**
   * Choosing the rule for a product that does not exist yet (issue #829). The panel reads nothing -
   * there is no stored rule, and a new product is created with none - so the picker is usable
   * immediately, it offers exactly the choices the edit page offers, and it writes nothing itself:
   * the component composing it persists the choice after the product has been created, which is the
   * only moment there is a product to write it to.
   */
  describe('choosing the rule for a product that does not exist yet (issue #829)', () => {
    it('reads no stored rule and offers the same three choices, ready to use', async () => {
      const { fixture, host, select, getGstRule } = await renderDeferred();

      expect(getGstRule).not.toHaveBeenCalled();
      expect(fixture.componentInstance.rule).toBe(GstClassification.Unknown);
      expect(Array.from(host.querySelectorAll('option')).map((option) => option.textContent?.trim())).toEqual([
        'No rule',
        'Taxable',
        'GST-free'
      ]);
      expect(select().disabled).toBe(false);
      expect(host.textContent).not.toContain('Failed to load the GST rule.');
    });

    it('reports each choice to the component it is composed into', async () => {
      const { fixture, choose, reported } = await renderDeferred();

      choose('Taxable');
      expect(reported).toEqual([GstClassification.Taxable]);
      expect(fixture.componentInstance.rule).toBe(GstClassification.Taxable);

      choose('GST-free');
      expect(reported).toEqual([GstClassification.Taxable, GstClassification.GstFree]);
    });

    it('offers no Save button of its own, because the rule is saved with the product', async () => {
      const { host } = await renderDeferred();

      expect(
        Array.from(host.querySelectorAll('button')).filter((button) => button.textContent?.trim().startsWith('Save'))
      ).toEqual([]);
    });

    it('writes nothing itself and is never counted as changed, even if asked to write', async () => {
      const { fixture, choose, setGstRule } = await renderDeferred();

      choose('Taxable');
      expect(fixture.componentInstance.dirty).toBe(false);
      fixture.componentInstance.persist().subscribe();

      expect(setGstRule).not.toHaveBeenCalled();
    });

    it('keeps the edit page’s help and adds that the rule is saved with the product', async () => {
      const { host } = await renderDeferred();

      expect(host.textContent).toContain('takes precedence over any supplier default');
      expect(host.textContent).toContain('Existing purchases keep their recorded GST treatment.');
      expect(host.textContent).toContain('"No rule" means unclassified, not GST-free.');
      expect(host.textContent).toContain('saved with the new product');
    });
  });

  describe('in a support session (issue #661)', () => {
    const loaded = () => jest.fn(() => of({ productId: 42, gstRule: GstClassification.Taxable }));

    it('calls no rule API even when asked to write a changed rule while a session is active', async () => {
      const setGstRule = jest.fn();
      const { fixture, persist } = await render(loaded(), setGstRule, of(true));

      fixture.componentInstance.rule = GstClassification.GstFree;
      const outcome = persist();

      expect(setGstRule).not.toHaveBeenCalled();
      expect(outcome.error).toBeInstanceOf(Error);
    });
  });
});
