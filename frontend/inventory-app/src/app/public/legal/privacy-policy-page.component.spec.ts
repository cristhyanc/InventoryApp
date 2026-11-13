import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LEGAL_EFFECTIVE_DATE } from './legal-effective-date';
import { PrivacyPolicyPageComponent } from './privacy-policy-page.component';

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function render(): HTMLElement {
  TestBed.configureTestingModule({ imports: [PrivacyPolicyPageComponent], providers: [provideRouter([])] });
  const fixture = TestBed.createComponent(PrivacyPolicyPageComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

/**
 * The published Privacy Policy (issue #1214): the owner-approved copy, with no placeholder left. The
 * layout itself - the table of contents, the numbered sections, the business details and the
 * keyboard behaviour - is `LegalPageComponent`'s own spec.
 */
describe('PrivacyPolicyPageComponent (issue #1214)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the Privacy Policy heading and the shared effective date, with no introduction', () => {
    const host = render();

    expect(text(host.querySelector('h1'))).toBe('Restockroo Privacy Policy');
    expect(text(host.querySelector('[data-testid="legal-effective-date"]'))).toBe(`Effective date: ${LEGAL_EFFECTIVE_DATE}`);
    expect(host.querySelector('[data-testid="legal-introduction"]')).toBeNull();
  });

  it('renders the approved eleven section headings in order', () => {
    const host = render();

    const headings = Array.from(host.querySelectorAll('section h2')).map((heading) => text(heading));
    expect(headings).toEqual([
      '1. Who we are',
      '2. Information we collect',
      '3. How we use information',
      '4. Who receives information',
      '5. AI and supplier-price collection',
      '6. Hosting and overseas processing',
      '7. Browser storage',
      '8. Security and retention',
      '9. Access, correction and deletion',
      '10. Questions and complaints',
      '11. Changes'
    ]);
  });

  it('carries no placeholder, draft review note or bracketed instruction', () => {
    const pageText = text(render());

    expect(pageText).not.toContain('[');
    expect(pageText).not.toMatch(/insert|placeholder|review notes|before publication|Approved content/i);
  });

  it('includes the receipt-scanning disclosure (issue #1202) in the AI section', () => {
    const section = render().querySelector('#ai-and-supplier-price-collection')!;

    expect(text(section)).toContain(
      'When you scan a receipt, Restockroo sends the receipt image or PDF, along with the names and IDs of your active products and suppliers, to Anthropic so that it can read the receipt and suggest matches.'
    );
    expect(text(section)).toContain('it is stored with the purchase when you save it unless you remove it first.');
  });

  it('lists what is collected as bullets, and links the contact email and the OAIC', () => {
    const host = render();

    expect(host.querySelectorAll('#information-we-collect li')).toHaveLength(6);
    expect(host.querySelector('#who-we-are a[href="mailto:admin@restockroo.com.au"]')).not.toBeNull();
    expect(host.querySelector('#questions-and-complaints a[href="https://www.oaic.gov.au"]')).not.toBeNull();
  });

  it('links onward to the Terms of Use page', () => {
    const crossLink = render().querySelector('[data-testid="legal-cross-link"]')!;

    expect(text(crossLink)).toBe('View Terms of Use');
    expect(crossLink.getAttribute('href')).toBe('/terms-of-use');
  });

  it('leaves out the export’s invented labels and actions', () => {
    const pageText = text(render());

    expect(pageText).not.toContain('Legal & Regulatory');
    expect(pageText).not.toContain('Restockroo Compliance Administration');
    expect(pageText).not.toContain('Contents Ledger');
    expect(pageText).not.toContain('Contact Privacy Desk');
    expect(pageText).not.toContain('Start free');
  });
});
