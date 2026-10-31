import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Product } from '../../../models/models';
import { PurchaseProductPickerComponent } from './purchase-product-picker.component';

function product(id: number, name: string, unit: string | null = null): Product {
  return { id, name, unit } as Product;
}

const PRODUCTS = [
  product(1, 'Red Bull Energy Drink', '250mL'),
  product(2, 'Red Bull Sugarfree', '250mL'),
  product(3, 'Coca-Cola Classic', null)
];

describe('PurchaseProductPickerComponent (issue #1029)', () => {
  let fixture: ComponentFixture<PurchaseProductPickerComponent>;
  let component: PurchaseProductPickerComponent;
  let input: HTMLInputElement;
  let chosen: number[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PurchaseProductPickerComponent] }).compileComponents();
    fixture = TestBed.createComponent(PurchaseProductPickerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('products', PRODUCTS);
    fixture.componentRef.setInput('productId', 0);
    fixture.componentRef.setInput('ariaLabel', 'Product for item 1');
    chosen = [];
    component.productIdChange.subscribe((id) => chosen.push(id));
    fixture.detectChanges();
    input = fixture.nativeElement.querySelector('input[role="combobox"]');
  });

  const options = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('[role="option"]'));

  function type(text: string): void {
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function key(name: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: name, cancelable: true });
    input.dispatchEvent(event);
    fixture.detectChanges();
    return event;
  }

  it('is a labelled combobox that starts empty and closed', () => {
    expect(input.getAttribute('aria-label')).toBe('Product for item 1');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(input.value).toBe('');
  });

  it('lists every product with its size or unit when opened', () => {
    input.dispatchEvent(new Event('focus'));
    fixture.detectChanges();

    expect(options().map((o) => o.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Red Bull Energy Drink, 250mL',
      'Red Bull Sugarfree, 250mL',
      'Coca-Cola Classic'
    ]);
  });

  it('narrows the list to the products whose name contains the search', () => {
    type('sugar');

    expect(options()).toHaveLength(1);
    expect(options()[0].textContent).toContain('Red Bull Sugarfree');
  });

  it('says so when nothing matches, and offers nothing invented', () => {
    type('pepsi');

    expect(options()).toHaveLength(0);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No product matches “pepsi”.');
  });

  it('chooses with the keyboard, without submitting the form, and shows the chosen name', () => {
    type('red');
    key('ArrowDown');
    const enter = key('Enter');

    expect(enter.defaultPrevented).toBe(true);
    expect(chosen).toEqual([2]);
    expect(input.value).toBe('Red Bull Sugarfree');
    expect(input.getAttribute('aria-expanded')).toBe('false');
  });

  it('chooses with a click or tap', () => {
    type('coca');
    options()[0].click();
    fixture.detectChanges();

    expect(chosen).toEqual([3]);
    expect(input.value).toBe('Coca-Cola Classic');
  });

  it('closes on Escape and puts the chosen product back in the box', () => {
    fixture.componentRef.setInput('productId', 1);
    fixture.detectChanges();
    type('zzz');

    key('Escape');

    expect(input.getAttribute('aria-expanded')).toBe('false');
    expect(input.value).toBe('Red Bull Energy Drink');
    expect(chosen).toEqual([]);
  });

  it('points at the active option while the list is open', () => {
    type('red');
    key('ArrowDown');

    const active = input.getAttribute('aria-activedescendant');
    expect(active).toBe(options()[1].id);
  });

  it('marks itself invalid when the line says so', () => {
    fixture.componentRef.setInput('invalid', true);
    fixture.componentRef.setInput('describedBy', 'line-0-product-error');
    fixture.detectChanges();

    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('line-0-product-error');
  });
});
