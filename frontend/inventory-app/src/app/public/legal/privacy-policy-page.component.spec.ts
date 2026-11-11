import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PrivacyPolicyPageComponent } from './privacy-policy-page.component';

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/**
 * The Privacy Policy page layout (issue #1190): the Stitch export's own headings and placeholders,
 * with no legal wording or invented date. The layout itself - the table of contents, the numbered
 * sections, the business details and the keyboard behaviour - is `LegalPageComponent`'s own spec.
 */
describe('PrivacyPolicyPageComponent (issue #1190)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the Privacy Policy heading, placeholder effective date and introduction', () => {
    TestBed.configureTestingModule({ imports: [PrivacyPolicyPageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PrivacyPolicyPageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(text(host.querySelector('h1'))).toBe('Restockroo Privacy Policy');
    expect(text(host.querySelector('[data-testid="legal-effective-date"]'))).toBe('[Effective date]');
    expect(text(host.querySelector('[data-testid="legal-introduction"]'))).toBe('[Approved content to be inserted]');
  });

  it('renders the export’s ten section headings in order, with the Sovereignty typo fixed', () => {
    TestBed.configureTestingModule({ imports: [PrivacyPolicyPageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PrivacyPolicyPageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const headings = Array.from(host.querySelectorAll('section h2')).map((heading) => text(heading));
    expect(headings).toEqual([
      '1. Who We Are and What This Covers',
      '2. Data We Collect',
      '3. Purpose of Processing',
      '4. Sub-processors & Infrastructure',
      '5. Automated Indexing & Receipts',
      '6. Data Sovereignty & Cloud Hosting',
      '7. Cookies & Local Session Cache',
      '8. Security & Ledger Retention',
      '9. Australian Privacy Rights',
      '10. Modifications & Revision Log'
    ]);
    expect(text(host).toLowerCase()).not.toContain('sovereignity');
  });

  it('links onward to the Terms of Use page', () => {
    TestBed.configureTestingModule({ imports: [PrivacyPolicyPageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PrivacyPolicyPageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const crossLink = host.querySelector('[data-testid="legal-cross-link"]')!;
    expect(text(crossLink)).toBe('View Terms of Use');
    expect(crossLink.getAttribute('href')).toBe('/terms-of-use');
  });

  it('leaves out the export’s invented labels and actions', () => {
    TestBed.configureTestingModule({ imports: [PrivacyPolicyPageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(PrivacyPolicyPageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const pageText = text(host);

    expect(pageText).not.toContain('Legal & Regulatory');
    expect(pageText).not.toContain('Restockroo Compliance Administration');
    expect(pageText).not.toContain('Restockroo Governance');
    expect(pageText).not.toContain('Contents Ledger');
    expect(pageText).not.toContain('Contact Privacy Desk');
    expect(pageText).not.toContain('Contact Support');
    expect(pageText).not.toContain('Start free');
  });
});
