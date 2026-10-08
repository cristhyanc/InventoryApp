import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { BreadcrumbService } from './breadcrumb.service';
import { BreadcrumbsComponent } from './breadcrumbs.component';

@Component({ standalone: true, template: '' })
class BlankPageComponent {}

interface Rendered {
  fixture: ComponentFixture<BreadcrumbsComponent>;
  host: HTMLElement;
  router: Router;
  breadcrumbService: BreadcrumbService;
}

async function render(url = '/'): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [BreadcrumbsComponent],
    providers: [provideRouter([{ path: '**', component: BlankPageComponent }])]
  }).compileComponents();

  const router = TestBed.inject(Router);
  await router.navigateByUrl(url);

  const fixture = TestBed.createComponent(BreadcrumbsComponent);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement, router, breadcrumbService: TestBed.inject(BreadcrumbService) };
}

function nav(host: HTMLElement): HTMLElement | null {
  return host.querySelector('nav[aria-label="Breadcrumb"]');
}

function items(host: HTMLElement): HTMLLIElement[] {
  return Array.from(host.querySelectorAll('ol li'));
}

describe('BreadcrumbsComponent (issue #457)', () => {
  it.each([
    ['a standalone top-level page', '/'],
    ['a URL with no breadcrumb mapping, inventing no label from the raw path', '/admin/some-unmapped-child'],
    ['a page with no mapped parent', '/machines']
  ])('renders nothing for %s', async (_description, url) => {
    const { host } = await render(url);

    expect(nav(host)).toBeNull();
  });

  it('renders a labelled landmark, an ordered list, a parent link and the current page as text', async () => {
    const { host } = await render('/products/42/edit');

    const landmark = nav(host);
    expect(landmark).not.toBeNull();
    expect(landmark?.querySelector('ol')).not.toBeNull();

    const rows = items(host);
    expect(rows).toHaveLength(2);

    const parentLink = rows[0].querySelector('a');
    expect(parentLink?.textContent?.trim()).toBe('Products');
    expect(parentLink?.getAttribute('href')).toBe('/products');
    expect(parentLink?.getAttribute('aria-current')).toBeNull();

    const current = rows[1].querySelector('a');
    expect(current).toBeNull();
    expect(rows[1].textContent).toContain('Edit product');
    expect(rows[1].querySelector('[aria-current="page"]')?.textContent?.trim()).toBe('Edit product');
  });

  it('hides the separator from assistive technology', async () => {
    const { host } = await render('/products/42/edit');

    const separators = host.querySelectorAll('[aria-hidden="true"]');
    expect(separators.length).toBeGreaterThan(0);
    Array.from(separators).forEach((separator) => expect(separator.textContent?.trim().length).toBeGreaterThan(0));
  });

  it('updates on in-app navigation to a different mapped route', async () => {
    const { host, fixture, router } = await render('/products/42/edit');
    expect(items(host).map((item) => item.textContent?.trim())).toEqual(
      expect.arrayContaining([expect.stringContaining('Products')])
    );

    await router.navigateByUrl('/admin/imports');
    fixture.detectChanges();

    const rows = items(host);
    expect(rows[0].textContent).toContain('Admin');
    expect(rows[1].textContent).toContain('Imports');
  });

  it('shows the generic label for a dynamic route until the page supplies a live one', async () => {
    const { host } = await render('/machines/7');

    expect(items(host)[1].textContent).toContain('Machine details');
  });

  it('shows the live label once the page sets it from already-loaded data, and current stays non-link text', async () => {
    const { host, fixture, breadcrumbService } = await render('/machines/7');

    breadcrumbService.setCurrentPageLabel('Snack Attack 42');
    fixture.detectChanges();

    expect(items(host)[1].textContent).toContain('Snack Attack 42');
    expect(items(host)[1].querySelector('a')).toBeNull();
  });

  it('resets to the generic label when a reused component navigates to a new id, never a stale name', async () => {
    const { host, fixture, router, breadcrumbService } = await render('/machines/7');
    breadcrumbService.setCurrentPageLabel('Snack Attack 42');

    await router.navigateByUrl('/machines/9');
    fixture.detectChanges();

    expect(items(host)[1].textContent).toContain('Machine details');
    expect(items(host)[1].textContent).not.toContain('Snack Attack 42');
  });
});
