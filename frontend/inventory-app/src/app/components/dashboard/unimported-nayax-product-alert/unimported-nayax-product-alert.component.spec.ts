import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { ProviderAvailabilityService } from '../../../provider/provider-availability.service';
import {
  CatalogueReviewAttentionService,
  UnimportedProviderProductAttention
} from '../../../services/catalogue-review-attention.service';
import { SupportSessionService } from '../../../support-session/support-session.service';
import { UnimportedNayaxProductAlertComponent } from './unimported-nayax-product-alert.component';

interface Options {
  attention?: Observable<UnimportedProviderProductAttention>;
  allowed?: boolean;
  providerOffered?: boolean;
  sessionActive?: boolean;
}

function attention(
  unimportedProductCount: number,
  examples: UnimportedProviderProductAttention['examples'] = []
): UnimportedProviderProductAttention {
  return { unimportedProductCount, examples };
}

async function render(options: Options = {}) {
  const read = jest.fn(() => options.attention ?? of(attention(0)));

  await TestBed.configureTestingModule({
    imports: [UnimportedNayaxProductAlertComponent],
    providers: [
      provideRouter([]),
      { provide: CatalogueReviewAttentionService, useValue: { read } },
      { provide: AccessService, useValue: { allows: () => of(options.allowed ?? true) } },
      {
        provide: ProviderAvailabilityService,
        useValue: { featureOffered: () => of(options.providerOffered ?? true) }
      },
      { provide: SupportSessionService, useValue: { active$: of(options.sessionActive ?? false) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(UnimportedNayaxProductAlertComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, read };
}

function text(host: HTMLElement, testId: string): string | undefined {
  return host.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
}

describe('UnimportedNayaxProductAlertComponent (issue #1352)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('names the products that are selling and links to Import products', async () => {
    const { host, fixture } = await render({
      attention: of(
        attention(2, [
          { nayaxProductId: '901', productName: 'Monster Original' },
          { nayaxProductId: '902', productName: 'Coke Zero 600ml' }
        ])
      )
    });

    expect(fixture.componentInstance.isShown).toBe(true);
    expect(text(host, 'unimported-nayax-product-alert-title')).toBe(
      "2 Nayax products are selling but aren't in your products"
    );
    expect(text(host, 'unimported-nayax-product-alert-detail')).toBe(
      'Monster Original, Coke Zero 600ml. These sales take no stock and carry no cost until the products are in Restockroo.'
    );
    expect(
      host.querySelector('[data-testid="unimported-nayax-product-alert-link"]')?.getAttribute('href')
    ).toBe('/products/import');
  });

  /** One product is one product, and the sentence about it has to read as one too. */
  it('says a single product in the singular', async () => {
    const { host } = await render({
      attention: of(attention(1, [{ nayaxProductId: '901', productName: 'Monster Original' }]))
    });

    expect(text(host, 'unimported-nayax-product-alert-title')).toBe(
      "1 Nayax product is selling but isn't in your products"
    );
    expect(text(host, 'unimported-nayax-product-alert-detail')).toBe(
      'Monster Original. This sale takes no stock and carries no cost until the product is in Restockroo.'
    );
  });

  /**
   * The endpoint names at most three products while counting every one of them, so the alert has to
   * account for the rest rather than under-report what is waiting.
   */
  it('counts every product and names the rest as "and N more"', async () => {
    const { host } = await render({
      attention: of(
        attention(5, [
          { nayaxProductId: '901', productName: 'Monster Original' },
          { nayaxProductId: '902', productName: 'Coke Zero 600ml' },
          { nayaxProductId: '903', productName: 'Chips' }
        ])
      )
    });

    expect(text(host, 'unimported-nayax-product-alert-title')).toBe(
      "5 Nayax products are selling but aren't in your products"
    );
    expect(text(host, 'unimported-nayax-product-alert-detail')).toContain(
      'Monster Original, Coke Zero 600ml, Chips and 2 more.'
    );
  });

  /** A sale that stored no name leaves the provider's id as the only honest thing to show. */
  it('falls back to the provider id when the sale stored no name', async () => {
    const { host } = await render({
      attention: of(attention(1, [{ nayaxProductId: '901', productName: null }]))
    });

    expect(text(host, 'unimported-nayax-product-alert-detail')).toContain('Nayax product 901.');
  });

  it('is hidden while every selling product is already imported', async () => {
    const { host, fixture } = await render({ attention: of(attention(0)) });

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="unimported-nayax-product-alert"]')).toBeNull();
  });

  it('is hidden, rather than claiming nothing is missing, when the read fails', async () => {
    const { host, fixture } = await render({ attention: throwError(() => new Error('offline')) });

    expect(fixture.componentInstance.isShown).toBe(false);
    expect(host.querySelector('[data-testid="unimported-nayax-product-alert"]')).toBeNull();
  });

  /**
   * The three gates, each of which must stop the request itself and not merely the rendering: a role
   * that may not run the import is refused by the endpoint anyway, a business with no Nayax
   * connection has no catalogue to import from, and a support session cannot run the import at all.
   */
  it.each([
    ['the member may not run imports', { allowed: false }],
    ['the business has no Nayax connection', { providerOffered: false }],
    ['a support session is active', { sessionActive: true }]
  ])('asks nothing and shows nothing while %s', async (_case, options: Options) => {
    const { host, read } = await render({
      ...options,
      attention: of(attention(3, [{ nayaxProductId: '901', productName: 'Monster Original' }]))
    });

    expect(read).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="unimported-nayax-product-alert"]')).toBeNull();
  });

  it('asks once when every gate allows it', async () => {
    const { read } = await render();

    expect(read).toHaveBeenCalledTimes(1);
  });
});
