import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { ProductGstRuleBulkComponent } from './product-gst-rule-bulk.component';
import { ProductService } from '../../../services/product.service';
import { BulkProductGstRuleApplied, GstClassification } from '../../../models/models';

/**
 * The prototype `<dialog>`'s methods, taken from an element rather than from the global so the spec
 * works whichever interface jsdom maps `dialog` to - the arrangement
 * `supplier-order-completion-dialog.component.spec.ts` uses.
 */
const dialogPrototype = Object.getPrototypeOf(document.createElement('dialog')) as HTMLDialogElement;

function conflict(message: string): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, error: { message } });
}

describe('ProductGstRuleBulkComponent (issue #1076)', () => {
  let previewBulkGstRule: jest.Mock;
  let setBulkGstRule: jest.Mock;

  beforeEach(() => {
    jest.spyOn(dialogPrototype, 'showModal').mockImplementation(() => undefined);
    jest.spyOn(dialogPrototype, 'close').mockImplementation(() => undefined);
    previewBulkGstRule = jest.fn(() => of({ eligibleCount: 4 }));
    setBulkGstRule = jest.fn(
      (gstRule: GstClassification, expectedEligibleCount: number) =>
        of<BulkProductGstRuleApplied>({ gstRule, changedCount: expectedEligibleCount })
    );
  });

  afterEach(() => {
    jest.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  async function render(open = true) {
    await TestBed.configureTestingModule({
      imports: [ProductGstRuleBulkComponent],
      providers: [{ provide: ProductService, useValue: { previewBulkGstRule, setBulkGstRule } }]
    }).compileComponents();

    const fixture: ComponentFixture<ProductGstRuleBulkComponent> =
      TestBed.createComponent(ProductGstRuleBulkComponent);
    const applied: number[] = [];
    const closed: number[] = [];
    fixture.componentInstance.applied.subscribe((count) => applied.push(count));
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    const setOpen = (value: boolean) => {
      fixture.componentRef.setInput('open', value);
      fixture.detectChanges();
    };
    setOpen(open);

    const host = fixture.nativeElement as HTMLElement;
    const button = (text: string) =>
      Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(
        (candidate) => candidate.textContent?.trim() === text
      );
    const click = (text: string) => {
      button(text)!.click();
      fixture.detectChanges();
    };
    const choose = (label: string) => {
      const radio = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="radio"]')).find(
        (candidate) => candidate.labels?.[0]?.textContent?.trim().startsWith(label)
      )!;
      radio.click();
      fixture.detectChanges();
    };
    const text = () => host.textContent ?? '';

    return { fixture, host, button, click, choose, text, setOpen, applied, closed };
  }

  it('renders nothing and reads nothing until it is opened', async () => {
    const { host } = await render(false);

    expect(host.querySelector('dialog')).toBeNull();
    expect(previewBulkGstRule).not.toHaveBeenCalled();
  });

  it('reads the eligible count once when it opens, and opens as a real modal', async () => {
    const { host } = await render();

    expect(previewBulkGstRule).toHaveBeenCalledTimes(1);
    expect(host.querySelector('dialog')).not.toBeNull();
    expect(dialogPrototype.showModal).toHaveBeenCalled();
  });

  /** A tax treatment is a decision: nothing is preselected, and nothing can be applied until it is made. */
  it('preselects no rule and will not continue until one is chosen', async () => {
    const { fixture, host, button, choose } = await render();

    expect(fixture.componentInstance.rule).toBeNull();
    expect(
      Array.from(host.querySelectorAll<HTMLInputElement>('input[type="radio"]')).some((radio) => radio.checked)
    ).toBe(false);
    expect(button('Continue')!.disabled).toBe(true);

    choose('Taxable');

    expect(button('Continue')!.disabled).toBe(false);
  });

  it('offers Taxable and GST-free and nothing else', async () => {
    const { host } = await render();

    const labels = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="radio"]')).map((radio) =>
      radio.labels?.[0]?.textContent?.trim()
    );
    expect(labels).toEqual(['Taxable', 'GST-free']);
  });

  /**
   * The scope has to be stated, because it is wider than what the page behind the dialog is
   * showing: every product of this business with no rule, inactive ones included, whatever the list
   * is filtered or paged to.
   */
  it('states the scope in so many words', async () => {
    const { text } = await render();

    expect(text()).toContain('No rule');
    expect(text()).toContain('inactive');
    expect(text().toLowerCase()).toContain('search');
    expect(text().toLowerCase()).toContain('filter');
  });

  it('shows the eligible count and the chosen rule before anything is written', async () => {
    const { click, choose, text } = await render();

    choose('GST-free');
    click('Continue');

    expect(text()).toContain('4');
    expect(text()).toContain('GST-free');
    expect(setBulkGstRule).not.toHaveBeenCalled();
  });

  it('writes nothing when the confirmation is cancelled, and reports that it closed', async () => {
    const { click, choose, applied, closed } = await render();

    choose('Taxable');
    click('Continue');
    click('Cancel');

    expect(setBulkGstRule).not.toHaveBeenCalled();
    expect(applied).toEqual([]);
    expect(closed).toEqual([1]);
  });

  it('applies the chosen rule with the count that was confirmed, and reports what changed', async () => {
    const { click, choose, applied, closed } = await render();

    choose('Taxable');
    click('Continue');
    click('Apply');

    expect(setBulkGstRule).toHaveBeenCalledTimes(1);
    expect(setBulkGstRule).toHaveBeenCalledWith(GstClassification.Taxable, 4);
    expect(applied).toEqual([4]);
    expect(closed).toEqual([1]);
  });

  /** A double click, or a second Enter, must not configure the catalogue twice. */
  it('refuses a second submission while one is in flight', async () => {
    const pending = new Subject<BulkProductGstRuleApplied>();
    setBulkGstRule = jest.fn(() => pending.asObservable());
    const { button, click, choose } = await render();

    choose('Taxable');
    click('Continue');
    click('Apply');
    button('Applying...')!.click();

    expect(setBulkGstRule).toHaveBeenCalledTimes(1);
    expect(button('Applying...')!.disabled).toBe(true);
  });

  /**
   * Nothing eligible is an answer, not an error: the dialog says so and offers no way to write.
   */
  it('shows an empty state and disables Apply when nothing is eligible', async () => {
    previewBulkGstRule = jest.fn(() => of({ eligibleCount: 0 }));
    const { button, text } = await render();

    expect(text()).toContain('Every product in this business already has a GST rule');
    expect(button('Continue')).toBeUndefined();
    expect(button('Apply')).toBeUndefined();
    expect(setBulkGstRule).not.toHaveBeenCalled();
  });

  it('shows an actionable message and offers Retry when the count cannot be read', async () => {
    previewBulkGstRule = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { button, click, text } = await render();

    expect(text()).toContain('Failed to read how many products have no GST rule');
    expect(button('Continue')).toBeUndefined();

    previewBulkGstRule.mockReturnValue(of({ eligibleCount: 2 }));
    click('Retry');

    expect(text()).toContain('2');
    expect(button('Continue')).toBeDefined();
  });

  /**
   * The conflict the server raises when the eligible set moved since the confirmation. The dialog
   * shows the server's own sentence, re-reads the count, and goes back to the choice so the
   * operator confirms the new scope rather than re-sending the old one.
   */
  it('shows the conflict, refreshes the count and requires a fresh confirmation', async () => {
    setBulkGstRule = jest.fn(() => throwError(() => conflict('The products with no GST rule changed.')));
    const { click, choose, text, applied, closed } = await render();

    choose('Taxable');
    click('Continue');
    previewBulkGstRule.mockReturnValue(of({ eligibleCount: 3 }));
    click('Apply');

    expect(text()).toContain('The products with no GST rule changed.');
    expect(previewBulkGstRule).toHaveBeenCalledTimes(2);
    expect(text()).toContain('3');
    expect(applied).toEqual([]);
    expect(closed).toEqual([]);

    // The choice is kept, and confirming again sends the refreshed count.
    setBulkGstRule.mockReturnValue(of({ gstRule: GstClassification.Taxable, changedCount: 3 }));
    click('Continue');
    click('Apply');

    expect(setBulkGstRule).toHaveBeenLastCalledWith(GstClassification.Taxable, 3);
    expect(applied).toEqual([3]);
    expect(closed).toEqual([1]);
  });

  it('keeps the dialog open and says what failed when the write fails', async () => {
    setBulkGstRule = jest.fn(() => throwError(() => new HttpErrorResponse({ status: 500 })));
    const { button, click, choose, text, applied, closed } = await render();

    choose('GST-free');
    click('Continue');
    click('Apply');

    expect(text()).toContain('Failed to set the GST rule');
    expect(applied).toEqual([]);
    expect(closed).toEqual([]);
    expect(button('Apply')!.disabled).toBe(false);
  });

  it('shows a refusal the server worded itself', async () => {
    setBulkGstRule = jest.fn(() =>
      throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'A bulk change must set a rule.' } }))
    );
    const { click, choose, text } = await render();

    choose('Taxable');
    click('Continue');
    click('Apply');

    expect(text()).toContain('A bulk change must set a rule.');
  });

  /** Re-opening asks the question again from the start, with nothing carried over. */
  it('starts over when it is closed and opened again', async () => {
    const { button, click, choose, setOpen, fixture } = await render();

    choose('Taxable');
    click('Continue');
    setOpen(false);
    previewBulkGstRule.mockReturnValue(of({ eligibleCount: 7 }));
    setOpen(true);

    expect(fixture.componentInstance.rule).toBeNull();
    expect(fixture.componentInstance.eligibleCount).toBe(7);
    expect(button('Continue')!.disabled).toBe(true);
    expect(previewBulkGstRule).toHaveBeenCalledTimes(2);
  });

  it('labels the dialog by its own heading', async () => {
    const { host } = await render();

    const dialog = host.querySelector('dialog') as HTMLDialogElement;
    const headingId = dialog.getAttribute('aria-labelledby');
    expect(headingId).toBeTruthy();
    expect(host.querySelector(`#${headingId}`)?.textContent).toBeTruthy();
  });
});
