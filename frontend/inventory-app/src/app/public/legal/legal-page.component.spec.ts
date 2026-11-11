import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LegalPageComponent } from './legal-page.component';
import { LegalPageContent } from './legal-page-content';

const CONTENT: LegalPageContent = {
  title: 'Restockroo Sample Policy',
  effectiveDateLabel: '[Effective date]',
  introduction: '[Approved content to be inserted]',
  sections: [
    { id: 'section-1', heading: 'First Section', body: '[Approved content to be inserted]' },
    { id: 'section-2', heading: 'Second Section', body: '[Approved content to be inserted]' }
  ]
};

function render(content: LegalPageContent = CONTENT): { fixture: ComponentFixture<LegalPageComponent>; host: HTMLElement } {
  TestBed.configureTestingModule({
    imports: [LegalPageComponent],
    providers: [provideRouter([])]
  });

  const fixture = TestBed.createComponent(LegalPageComponent);
  fixture.componentRef.setInput('content', content);
  fixture.componentRef.setInput('crossLinkRoute', 'terms-of-use');
  fixture.componentRef.setInput('crossLinkLabel', 'View Terms of Use');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/**
 * The shared legal page layout both the Privacy Policy and Terms of Use pages render through
 * (issue #1190). Each page's own spec covers its actual headings and placeholders; this covers the
 * layout every page shares: the table of contents, the numbered sections, the business display
 * details, the back-to-home and cross-page links, and keyboard navigation to a section.
 */
describe('LegalPageComponent (issue #1190)', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('renders the content title as the one h1 and the effective-date line', () => {
    const { host } = render();

    const headings = host.querySelectorAll('h1');
    expect(headings).toHaveLength(1);
    expect(text(headings[0])).toBe('Restockroo Sample Policy');
    expect(text(host.querySelector('[data-testid="legal-effective-date"]'))).toBe('[Effective date]');
  });

  it('renders the introduction when the content has one', () => {
    const { host } = render();
    expect(text(host.querySelector('[data-testid="legal-introduction"]'))).toBe('[Approved content to be inserted]');
  });

  it('renders no introduction when the content has none', () => {
    TestBed.resetTestingModule();
    const { host } = render({ ...CONTENT, introduction: undefined });
    expect(host.querySelector('[data-testid="legal-introduction"]')).toBeNull();
  });

  it('builds the table of contents from the sections, each link matching an in-page anchor', () => {
    const { host } = render();

    const tocLinks = Array.from(host.querySelectorAll<HTMLAnchorElement>('[data-testid="legal-toc"] a'));
    expect(tocLinks.map((link) => text(link))).toEqual(['First Section', 'Second Section']);
    expect(tocLinks.map((link) => link.getAttribute('href'))).toEqual(['#section-1', '#section-2']);

    for (const section of CONTENT.sections) {
      expect(host.querySelector(`#${section.id}`)).not.toBeNull();
    }
  });

  it('numbers the sections in order, each with its heading and body', () => {
    const { host } = render();

    const headings = Array.from(host.querySelectorAll('section h2')).map((heading) => text(heading));
    expect(headings).toEqual(['1. First Section', '2. Second Section']);

    const bodies = Array.from(host.querySelectorAll('section p')).map((body) => text(body));
    expect(bodies).toEqual(['[Approved content to be inserted]', '[Approved content to be inserted]']);
  });

  it('shows the exact business display details, with a plain mailto link', () => {
    const { host } = render();

    const contact = host.querySelector('[data-testid="legal-contact"]')!;
    expect(text(contact)).toContain('Eunhye Chung');
    expect(text(contact)).toContain('ABN 69 235 469 430');

    const email = host.querySelector<HTMLAnchorElement>('[data-testid="legal-contact-email"]')!;
    expect(text(email)).toBe('admin@restockroo.com.au');
    expect(email.getAttribute('href')).toBe('mailto:admin@restockroo.com.au');
  });

  it('links back to the homepage from the brand and the back-to-home link', () => {
    const { host } = render();

    expect(host.querySelector('[data-testid="legal-brand"]')?.getAttribute('href')).toBe('/home');
    expect(host.querySelector('[data-testid="legal-back-home"]')?.getAttribute('href')).toBe('/home');
  });

  it('links to the other legal page with the given label and route', () => {
    const { host } = render();

    const crossLink = host.querySelector('[data-testid="legal-cross-link"]')!;
    expect(text(crossLink)).toBe('View Terms of Use');
    expect(crossLink.getAttribute('href')).toBe('/terms-of-use');
  });

  it('scrolls to the chosen section on a table-of-contents click and moves focus to it', () => {
    const { host, fixture } = render();
    const section = host.querySelector<HTMLElement>('#section-2')!;
    const scrollIntoView = jest.fn();
    section.scrollIntoView = scrollIntoView;
    document.body.appendChild(host);

    const link = Array.from(host.querySelectorAll<HTMLAnchorElement>('[data-testid="legal-toc"] a')).find(
      (candidate) => text(candidate) === 'Second Section'
    )!;
    link.click();
    fixture.detectChanges();

    expect(scrollIntoView).toHaveBeenCalledWith(expect.objectContaining({ block: 'start' }));
    expect(section.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(section);
    host.remove();
  });
});
