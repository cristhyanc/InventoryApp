import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { ProductCreateGstRuleComponent } from './product-create-gst-rule.component';
import { ProductService } from '../../../services/product.service';
import { GstClassification } from '../../../models/models';
import { SupportSessionService } from '../../../support-session/support-session.service';

/**
 * The GST rule of a product being created (issue #829), on its own: the choice, the one write to
 * `PUT api/products/{id}/gst-rule` once the page says a product exists, the partial failure that
 * write can leave behind, and the retry of that write alone.
 */
async function render(
  overrides: {
    setGstRule?: jest.Mock;
    getGstRule?: jest.Mock;
    sessionActive?: Observable<boolean>;
  } = {}
) {
  const setGstRule = overrides.setGstRule ?? jest.fn(() => of(undefined));
  const getGstRule = overrides.getGstRule ?? jest.fn();

  await TestBed.configureTestingModule({
    imports: [ProductCreateGstRuleComponent],
    providers: [
      { provide: ProductService, useValue: { getGstRule, setGstRule } },
      { provide: SupportSessionService, useValue: { active$: overrides.sessionActive ?? of(false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductCreateGstRuleComponent);
  let persisted = 0;
  let failed = 0;
  fixture.componentInstance.persisted.subscribe(() => persisted++);
  fixture.componentInstance.failed.subscribe(() => failed++);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;

  /** Chooses a rule the way a person does, in the composed panel's own picker. */
  const choose = (label: string) => {
    const picker = host.querySelector<HTMLSelectElement>('app-product-gst-rule select')!;
    picker.selectedIndex = Array.from(picker.options).findIndex((option) => option.textContent?.trim() === label);
    picker.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  };

  /** What the page does when `POST api/products` has answered an id. */
  const productCreated = (id: number) => {
    fixture.componentRef.setInput('createdProductId', id);
    fixture.detectChanges();
  };

  const retry = () => host.querySelector<HTMLButtonElement>('button');

  return {
    fixture,
    component: fixture.componentInstance,
    host,
    choose,
    productCreated,
    retry,
    getGstRule,
    setGstRule,
    outcomes: () => ({ persisted, failed })
  };
}

afterEach(() => TestBed.resetTestingModule());

describe('ProductCreateGstRuleComponent (issue #829)', () => {
  const refused = () => jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));

  it('offers the edit page’s choices and help, reads nothing, and offers no write before a product exists', async () => {
    const { host, retry, getGstRule, setGstRule } = await render();

    expect(Array.from(host.querySelectorAll('option')).map((option) => option.textContent?.trim())).toEqual([
      'No rule',
      'Taxable',
      'GST-free'
    ]);
    expect(host.textContent).toContain('takes precedence over any supplier default');
    expect(host.textContent).toContain('does not change the GST classification of any');
    expect(host.querySelector<HTMLSelectElement>('app-product-gst-rule select')?.disabled).toBe(false);

    // There is no product yet: nothing to read a rule from and nothing to write one to.
    expect(getGstRule).not.toHaveBeenCalled();
    expect(setGstRule).not.toHaveBeenCalled();
    expect(retry()).toBeNull();
  });

  it('writes the chosen rule to the product that was created, then reports the create finished', async () => {
    const { choose, productCreated, setGstRule, outcomes } = await render();

    choose('Taxable');
    productCreated(55);

    expect(setGstRule).toHaveBeenCalledTimes(1);
    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.Taxable);
    expect(outcomes()).toEqual({ persisted: 1, failed: 0 });
  });

  it('writes GST-free as GST-free, never as the unclassified state', async () => {
    const { choose, productCreated, setGstRule } = await render();

    choose('GST-free');
    productCreated(55);

    expect(setGstRule).toHaveBeenCalledWith(55, GstClassification.GstFree);
  });

  /**
   * A created product already carries `GstRules.None`, so "no rule" is stored by the create itself:
   * a second write would state nothing, and the create is finished as it stands.
   */
  it('writes nothing for "No rule", because the create already stored it, and still reports finished', async () => {
    const { choose, productCreated, setGstRule, outcomes } = await render();

    choose('No rule');
    productCreated(55);

    expect(setGstRule).not.toHaveBeenCalled();
    expect(outcomes()).toEqual({ persisted: 1, failed: 0 });
  });

  it('reports the partial failure in its own words, offers the retry, and never reports finished', async () => {
    const { host, choose, productCreated, retry, outcomes } = await render({ setGstRule: refused() });

    choose('Taxable');
    productCreated(55);

    expect(outcomes()).toEqual({ persisted: 0, failed: 1 });
    expect(host.textContent).toContain('The product was created, but its GST rule was not saved.');
    expect(host.textContent).toContain('never creates a second product');
    expect(retry()?.textContent).toContain('Save GST rule');
    expect(retry()?.disabled).toBe(false);
  });

  /**
   * The retry is the reason this write lives here rather than on the page: it must re-send the rule
   * the person chose, which means nothing may re-read the created product's stored rule - still
   * unset, because this is the write that failed - over that choice. The composed panel is deferred
   * and reads nothing, and this asserts it stays that way.
   */
  it('retries that write alone, with the rule on screen now and no re-read of the stored rule', async () => {
    const setGstRule = jest
      .fn()
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })))
      .mockReturnValueOnce(of(undefined));
    const { fixture, host, choose, productCreated, retry, getGstRule, outcomes } = await render({ setGstRule });

    choose('Taxable');
    productCreated(55);
    expect(fixture.componentInstance.rule).toBe(GstClassification.Taxable);

    choose('GST-free');
    retry()!.click();
    fixture.detectChanges();

    expect(setGstRule).toHaveBeenCalledTimes(2);
    expect(setGstRule).toHaveBeenLastCalledWith(55, GstClassification.GstFree);
    expect(getGstRule).not.toHaveBeenCalled();
    expect(outcomes()).toEqual({ persisted: 1, failed: 1 });
    expect(host.textContent).not.toContain('The product was created, but its GST rule was not saved.');
  });

  it('issues no second write while one is still in flight', async () => {
    const setGstRule = jest.fn(() => NEVER);
    const { fixture, choose, productCreated, retry } = await render({ setGstRule });

    choose('Taxable');
    productCreated(55);

    expect(retry()?.disabled).toBe(true);
    fixture.componentInstance.save();

    expect(setGstRule).toHaveBeenCalledTimes(1);
  });

  it('writes nothing while no product exists, even if its guarded handler is invoked', async () => {
    const { component, choose, setGstRule, outcomes } = await render();

    choose('Taxable');
    component.save();

    expect(setGstRule).not.toHaveBeenCalled();
    expect(outcomes()).toEqual({ persisted: 0, failed: 0 });
  });

  /**
   * A support session grants `InventoryOperate`, the capability this resource is gated on, so the
   * write would otherwise be offered; the backend refuses it only by its read-only method rule
   * (issues #554/#661). The page refuses the create during a session as well, so the input below is
   * a state a person cannot reach - which is exactly why it is asserted to write nothing.
   */
  it('writes nothing while a support session is active, and offers no usable retry', async () => {
    const { component, choose, productCreated, retry, setGstRule, outcomes } = await render({
      sessionActive: of(true),
      setGstRule: refused()
    });

    choose('Taxable');
    productCreated(55);

    expect(setGstRule).not.toHaveBeenCalled();
    expect(outcomes()).toEqual({ persisted: 0, failed: 0 });
    expect(retry()?.disabled).toBe(true);

    component.save();
    expect(setGstRule).not.toHaveBeenCalled();
  });
});
