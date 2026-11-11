import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TermsOfUsePageComponent } from './terms-of-use-page.component';

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/**
 * The Terms of Use page layout (issue #1190): the Stitch export's own clause headings and
 * placeholders, with no legal wording or invented date. The layout itself - the table of contents,
 * the numbered sections, the business details and the keyboard behaviour - is
 * `LegalPageComponent`'s own spec.
 */
describe('TermsOfUsePageComponent (issue #1190)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the Terms of Use heading and placeholder effective date, with no introduction', () => {
    TestBed.configureTestingModule({ imports: [TermsOfUsePageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(TermsOfUsePageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(text(host.querySelector('h1'))).toBe('Restockroo Terms of Use');
    expect(text(host.querySelector('[data-testid="legal-effective-date"]'))).toBe('[Effective date]');
    expect(host.querySelector('[data-testid="legal-introduction"]')).toBeNull();
  });

  it('renders the export’s ten clause headings in order', () => {
    TestBed.configureTestingModule({ imports: [TermsOfUsePageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(TermsOfUsePageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const headings = Array.from(host.querySelectorAll('section h2')).map((heading) => text(heading));
    expect(headings).toEqual([
      '1. Agreement to Terms',
      '2. Description of the Service',
      '3. Public Beta Access & Free Pro Tier',
      '4. User Accounts & Responsibilities',
      '5. Acceptable Use Policy',
      '6. Data Ownership & Intellectual Property',
      '7. Third-Party Integrations & Supplier Data',
      '8. Service Availability & Beta Disclaimers',
      '9. Termination & Account Closure',
      '10. Governing Law & Dispute Resolution'
    ]);
  });

  it('links back to the Privacy Policy page', () => {
    TestBed.configureTestingModule({ imports: [TermsOfUsePageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(TermsOfUsePageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    const crossLink = host.querySelector('[data-testid="legal-cross-link"]')!;
    expect(text(crossLink)).toBe('View Privacy Policy');
    expect(crossLink.getAttribute('href')).toBe('/privacy-policy');
  });

  it('leaves out the export’s invented labels and actions', () => {
    TestBed.configureTestingModule({ imports: [TermsOfUsePageComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(TermsOfUsePageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const pageText = text(host);

    expect(pageText).not.toContain('Restockroo Governance');
    expect(pageText).not.toContain('Contact Support');
    expect(pageText).not.toContain('Stock management, supplier price comparisons');
  });
});
