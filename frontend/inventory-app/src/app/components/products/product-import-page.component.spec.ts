import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { ProductImportPageComponent } from './product-import-page.component';
import { CatalogueReviewService } from '../../services/catalogue-review.service';
import { CategoryService } from '../../services/category.service';
import { ProductService } from '../../services/product.service';
import { ToastService } from '../../services/toast.service';
import { SupportSessionService } from '../../support-session/support-session.service';

/**
 * The dedicated Import products page (issue #888). The catalogue review it hosts has its own spec
 * and is deliberately **not** stubbed out here: the point of this page is that it composes the real
 * `CatalogueReviewComponent` and changes nothing about it, so the review's own service is stubbed to
 * the calls it makes on load and what these tests assert is the page's own behaviour.
 */
const startReview = jest.fn(() =>
  of({
    reviewId: 1,
    startedAtUtc: '2026-10-25T02:00:00Z',
    categories: [],
    products: [],
    unitVolumeNote: '',
    unlinkedCategoryCount: 0,
    unlinkedProductCount: 0,
    unresolvedSaleCount: 0
  }));

async function render(sessionActive: Observable<boolean> = of(false)) {
  startReview.mockClear();
  await TestBed.configureTestingModule({
    imports: [ProductImportPageComponent],
    providers: [
      provideRouter([]),
      { provide: CatalogueReviewService, useValue: { start: startReview, apply: jest.fn() } },
      { provide: ProductService, useValue: { getAll: () => of([]) } },
      { provide: CategoryService, useValue: { getAll: () => of([]) } },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn() } },
      { provide: SupportSessionService, useValue: { active$: sessionActive } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(ProductImportPageComponent);
  const router = TestBed.inject(Router);
  const navigate = jest.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, navigate };
}

describe('ProductImportPageComponent (issue #888)', () => {
  it('renders the review on load, so it reads the Nayax catalogue exactly as it did in Admin → Imports', async () => {
    const { host } = await render();

    expect(host.querySelector('[data-testid="catalogue-review"]')).toBeTruthy();
    expect(startReview).toHaveBeenCalledTimes(1);
  });

  it('shows its own header with a Back to Products link, a title and a one-line subtitle', async () => {
    const { host } = await render();

    expect(host.querySelector('.page-title')?.textContent?.trim()).toBe('Import products');
    const back = host.querySelector('header a');
    expect(back?.getAttribute('href')).toBe('/products');
    expect(back?.textContent).toContain('Back to Products');
    expect(host.querySelector('.page-subtitle')?.textContent?.trim()).not.toBe('');
  });

  it('renders no review during a support session, and says imports are unavailable instead', async () => {
    const { host } = await render(of(true));

    expect(host.querySelector('[data-testid="catalogue-review"]')).toBeNull();
    expect(startReview).not.toHaveBeenCalled();
    const notice = host.querySelector('[data-testid="import-support-session-notice"]');
    expect(notice?.textContent).toContain('support session');
  });

  it('navigates back to Products when the review asks to be closed', async () => {
    const { fixture, navigate } = await render();

    fixture.componentInstance.close();

    expect(navigate).toHaveBeenCalledWith('/products');
  });
});
