import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LEGAL_EFFECTIVE_DATE } from './legal-effective-date';
import { TermsOfUsePageComponent } from './terms-of-use-page.component';

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function render(): HTMLElement {
  TestBed.configureTestingModule({ imports: [TermsOfUsePageComponent], providers: [provideRouter([])] });
  const fixture = TestBed.createComponent(TermsOfUsePageComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/**
 * The published Terms of Use (issue #1214): the owner-approved copy, with no placeholder left. The
 * layout itself is `LegalPageComponent`'s own spec.
 */
describe('TermsOfUsePageComponent (issue #1214)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the Terms of Use heading and the shared effective date, with no introduction', () => {
    const host = render();

    expect(text(host.querySelector('h1'))).toBe('Restockroo Terms of Use');
    expect(text(host.querySelector('[data-testid="legal-effective-date"]'))).toBe(`Effective date: ${LEGAL_EFFECTIVE_DATE}`);
    expect(host.querySelector('[data-testid="legal-introduction"]')).toBeNull();
  });

  it('renders the approved eleven clause headings in order', () => {
    const host = render();

    const headings = Array.from(host.querySelectorAll('section h2')).map((heading) => text(heading));
    expect(headings).toEqual([
      '1. Agreement and operator',
      '2. The service',
      '3. Free introductory Pro access',
      '4. Accounts and authorised users',
      '5. Acceptable use',
      '6. Your data and our software',
      '7. Integrations, prices and AI outputs',
      '8. Beta operation and changes',
      '9. Suspension and closure',
      '10. Rights and responsibility',
      '11. Disputes and applicable law'
    ]);
  });

  it('carries no placeholder, draft review note or implementation note', () => {
    const pageText = text(render());

    expect(pageText).not.toContain('[');
    expect(pageText).not.toMatch(/insert|placeholder|review notes|implementation notes|evidence reviewed|Approved content/i);
  });

  it('includes the supplier price comparison clause (issue #1183) word for word in section 7', () => {
    const section = text(render().querySelector('#integrations-prices-and-ai-outputs'));

    expect(section).toContain(
      'Supplier comparisons are based on recorded offers from supported suppliers and do not cover every supplier or guarantee the lowest final purchase cost. Prices, availability and product matches may be incomplete, delayed or incorrect, including where automated collection or matching is used. Before purchasing, verify the exact product, pack size, GST treatment, location or membership conditions, availability, delivery charges and final total directly with the supplier. Nothing in these terms excludes, restricts or modifies rights or remedies that cannot lawfully be excluded.'
    );
  });

  it('links onward to the Privacy Policy page', () => {
    const crossLink = render().querySelector('[data-testid="legal-cross-link"]')!;

    expect(text(crossLink)).toBe('View Privacy Policy');
    expect(crossLink.getAttribute('href')).toBe('/privacy-policy');
  });

  it('leaves out the export’s invented labels and actions', () => {
    const pageText = text(render());

    expect(pageText).not.toContain('Restockroo Governance');
    expect(pageText).not.toContain('Contact Support');
    expect(pageText).not.toContain('Start free');
    expect(pageText).not.toContain('Stock management, supplier price comparisons and refill planning for vending operators.');
  });
});
