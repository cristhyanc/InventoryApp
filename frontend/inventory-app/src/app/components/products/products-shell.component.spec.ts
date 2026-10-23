import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ProductsShellComponent } from './products-shell.component';

/**
 * The shared Products header (issue #825). "New Product" used to live here, which put a create
 * action on every view the shell wraps - including Needs Ordering, which orders products that
 * already exist and never creates one. It moved into the All Products search/filter toolbar
 * (`ProductListComponent`), so the shell now carries only the page title and the view navigation.
 */
describe('ProductsShellComponent (issue #825)', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    await TestBed.configureTestingModule({
      imports: [ProductsShellComponent],
      providers: [provideRouter([])]
    }).compileComponents();

    const fixture = TestBed.createComponent(ProductsShellComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('carries no New Product action, so no Products view inherits one from the header', async () => {
    const { host } = await render();

    expect(host.querySelector('a[href="/products/new"]')).toBeNull();
    expect(host.textContent).not.toContain('New Product');
  });

  it('keeps the page title and the three view links', async () => {
    const { host } = await render();

    expect(host.querySelector('h1.page-title')?.textContent?.trim()).toBe('Products');
    expect(
      Array.from(host.querySelectorAll('nav a')).map((link) => link.getAttribute('href'))
    ).toEqual(['/products', '/products/needs-ordering', '/purchases/orders']);
  });
});
