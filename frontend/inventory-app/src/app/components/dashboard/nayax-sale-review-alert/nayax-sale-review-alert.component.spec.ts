import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { NayaxSaleReviewService, NayaxSaleReviewSummary } from '../../../services/nayax-sale-review.service';
import { NayaxSaleReviewAlertComponent } from './nayax-sale-review-alert.component';

async function render(summary: Observable<NayaxSaleReviewSummary>) {
  await TestBed.configureTestingModule({
    imports: [NayaxSaleReviewAlertComponent],
    providers: [provideRouter([]), { provide: NayaxSaleReviewService, useValue: { summary: () => summary } }]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxSaleReviewAlertComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

describe('NayaxSaleReviewAlertComponent (issue #1105)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('shows the review and not-counted counts and links to the review page', async () => {
    const { host, fixture } = await render(of({ needsReviewCount: 3, notCountedCount: 1 }));

    expect(fixture.componentInstance.isShown).toBe(true);
    expect(host.querySelector('[data-testid="nayax-sale-review-alert-title"]')?.textContent?.trim()).toBe(
      '3 Nayax sales need your review · 1 is not being counted'
    );
    expect(host.querySelector('[data-testid="nayax-sale-review-alert-link"]')?.getAttribute('href')).toBe(
      '/nayax-sales-review'
    );
  });

  it('leaves out the not-counted part when every flagged sale still counts', async () => {
    const { host } = await render(of({ needsReviewCount: 1, notCountedCount: 0 }));

    expect(host.querySelector('[data-testid="nayax-sale-review-alert-title"]')?.textContent?.trim()).toBe(
      '1 Nayax sale needs your review'
    );
  });

  it('says plural not-counted sales in the plural', async () => {
    const { fixture } = await render(of({ needsReviewCount: 4, notCountedCount: 2 }));

    expect(fixture.componentInstance.title).toBe('4 Nayax sales need your review · 2 are not being counted');
  });

  it('is hidden while nothing waits for the owner', async () => {
    const { host, fixture } = await render(of({ needsReviewCount: 0, notCountedCount: 0 }));

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="nayax-sale-review-alert"]')).toBeNull();
  });

  it('is hidden, rather than claiming nothing to review, when the read fails', async () => {
    const { host, fixture } = await render(throwError(() => new Error('offline')));

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="nayax-sale-review-alert"]')).toBeNull();
  });
});
