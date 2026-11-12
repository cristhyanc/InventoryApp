import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { AccountInfo } from '@azure/msal-browser';
import { of } from 'rxjs';
import { configureExternalId, unconfigureExternalId } from '../../auth/external-id.spec-helpers';
import { LANDING_DESCRIPTION, LANDING_TITLE } from './landing-content';
import { LandingPageComponent } from './landing-page.component';

const account = { homeAccountId: 'dana', username: 'dana@example.invalid', name: 'Dana' } as AccountInfo;

interface Rendered {
  fixture: ComponentFixture<LandingPageComponent>;
  host: HTMLElement;
  loginRedirect: jest.Mock;
  navigateByUrl: jest.SpyInstance;
}

async function render(options: { signedIn?: boolean } = {}): Promise<Rendered> {
  const loginRedirect = jest.fn();
  await TestBed.configureTestingModule({
    imports: [LandingPageComponent],
    providers: [
      provideRouter([]),
      provideHttpClient(),
      provideHttpClientTesting(),
      {
        provide: MsalService,
        useValue: {
          instance: { getAllAccounts: () => (options.signedIn ? [account] : []) },
          handleRedirectObservable: () => of(null),
          loginRedirect
        }
      }
    ]
  }).compileComponents();

  const navigateByUrl = jest.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(LandingPageComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, loginRedirect, navigateByUrl };
}

function text(element: Element | null | undefined): string {
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function byTestId<T extends HTMLElement = HTMLElement>(host: HTMLElement, id: string): T | null {
  return host.querySelector<T>(`[data-testid="${id}"]`);
}

describe('LandingPageComponent (issue #954)', () => {
  afterEach(() => {
    // The page is public and static: it must never read business, account, access or Nayax data.
    TestBed.inject(HttpTestingController).verify();
    unconfigureExternalId();
    TestBed.resetTestingModule();
  });

  describe('content', () => {
    it('has one h1, carrying the public beta headline', async () => {
      const { host } = await render();

      const headings = host.querySelectorAll('h1');
      expect(headings).toHaveLength(1);
      expect(text(headings[0])).toBe('Know what to buy. Pack what you need.');
    });

    it('shows the public beta lead under the headline', async () => {
      const { host } = await render();

      const lead = Array.from(host.querySelectorAll('main section p')).find(
        (p) => text(p) === 'Manage vending stock, compare supplier prices and plan your next refill in one place.'
      );
      expect(lead).toBeDefined();
    });

    it('shows the beta-access notice and the export notice directly under it, near the hero', async () => {
      const { host } = await render();

      const betaNotice = byTestId(host, 'landing-beta-notice')!;
      const exportNotice = byTestId(host, 'landing-export-notice')!;
      expect(text(betaNotice)).toBe('Pro access is free during our public beta. No credit card required.');
      expect(text(exportNotice)).toBe(
        'Data exports are not part of the free beta. Downloading your data as CSV or Excel will need a paid plan once paid plans launch.'
      );
      expect(betaNotice.nextElementSibling).toBe(exportNotice);
    });

    it('states no price, no beta end date and no checkout anywhere on the page', async () => {
      const { host } = await render();
      const page = text(host).toLowerCase();

      for (const claim of ['a$', 'plan card', 'checkout', 'ends on', 'beta ends', 'until ']) {
        expect(page).not.toContain(claim);
      }
    });

    it('keeps the approved section order, with the section labels of the approved mobile design', async () => {
      const { host } = await render();

      const sectionIds = Array.from(host.querySelectorAll('main section[id]')).map((section) => section.id);
      expect(sectionIds).toEqual(['features', 'nayax', 'how-it-works', 'faqs']);

      const eyebrows = Array.from(host.querySelectorAll('main section p:first-child'))
        .map((label) => text(label))
        .filter((label) =>
          ['Practical inventory management', 'Features', 'Optional integration', 'How it works', 'Frequently asked questions'].includes(label)
        );
      expect(eyebrows).toEqual(['Practical inventory management', 'Features', 'Optional integration', 'How it works', 'Frequently asked questions']);
    });

    it('drops the fleet metrics, old sample sites and machines, and vehicle/route/tray/coil/slot language (issue #1192)', async () => {
      const { host } = await render();
      const page = text(host).toLowerCase();

      for (const claim of [
        'harbour point plaza',
        'apex commercial',
        'beverage combo',
        '922 items',
        'low-stock products',
        'vehicle capacity',
        'route frequency',
        'tray',
        'coil',
        'slot'
      ]) {
        expect(page).not.toContain(claim);
      }
    });

    it('offers the six feature cards', async () => {
      const { host } = await render();

      const titles = Array.from(host.querySelectorAll('[data-testid="landing-features"] h3')).map(text);
      expect(titles).toEqual([
        'Products and categories',
        'Stock on hand and stock movements',
        'Purchases and receipts',
        'Machines and sites',
        'Inventory value at cost',
        'Suppliers and ordering'
      ]);
    });

    it('describes Nayax as optional, with core inventory working on its own', async () => {
      const { host } = await render();

      expect(text(host.querySelector('#nayax h2'))).toBe('Use core inventory on its own. Connect Nayax for sales integration.');
      expect(text(host.querySelector('#how-it-works'))).toContain(
        'Running vending machines? Connect Nayax for supported sales and reporting features.'
      );
      expect(text(host.querySelector('#faqs'))).toContain('No. Core inventory features work without connecting a sales provider.');
    });

    it('never ties Nayax to a paid plan on the public beta page', async () => {
      const { host } = await render();
      const nayaxSection = text(host.querySelector('#nayax')).toLowerCase();

      for (const claim of ['pro plan', 'pro feature', 'paid plan', 'starter', 'once paid plans']) {
        expect(nayaxSection).not.toContain(claim);
      }
    });

    it('lists the three workflow steps in order', async () => {
      const { host } = await render();

      const steps = Array.from(host.querySelectorAll('[data-testid="landing-steps"] h3')).map(text);
      expect(steps).toEqual(['Step 01: Set up your business', 'Step 02: Add products and machines', 'Step 03: Plan and record refills']);
    });

    it('answers the FAQs without claiming a native app, and drops the three paid-plan FAQs', async () => {
      const { host } = await render();

      const questions = Array.from(host.querySelectorAll('[data-testid="landing-faqs"] h3')).map(text);
      expect(questions).toEqual([
        'Do I need Nayax?',
        'Is Restockroo suitable for vending businesses?',
        'Can I manage purchases and suppliers?',
        'Can I use Restockroo on my phone?'
      ]);
      const faqText = text(host.querySelector('[data-testid="landing-faqs"]'));
      expect(faqText).toContain('There is no app to install.');
      expect(faqText).not.toContain('When can I subscribe');
      expect(faqText).not.toContain('storage limit');
      expect(faqText).not.toContain('cost extra');
    });

    it('makes no claim of receipt scanning or automatic reading of purchase documents', async () => {
      const { host } = await render();
      const page = text(host).toLowerCase();

      for (const claim of ['scan', 'ocr', 'automatically read', 'auto-fill']) {
        expect(page).not.toContain(claim);
      }
      expect(page).toContain('attach the receipt');
    });

    it('makes none of the claims the issue rules out', async () => {
      const { host } = await render();
      const page = text(host).toLowerCase();

      for (const claim of [
        'offline',
        'telemetry',
        'real-time',
        'per-slot',
        'net profit',
        'partner',
        'free trial',
        'add-on',
        'testimonial',
        'waitlist',
        'checkout',
        'pricing',
        'plan card',
        'starter',
        'a$',
        'beta ends',
        'ends on'
      ]) {
        expect(page).not.toContain(claim);
      }
    });

    it('shows the footer contact details, no Privacy or Terms link, and dates the copyright this year', async () => {
      const { host } = await render();
      const footer = host.querySelector('footer')!;

      expect(text(footer)).not.toMatch(/privacy|terms/i);
      expect(text(footer)).toContain(`© ${new Date().getFullYear()} Restockroo`);

      const mailto = footer.querySelector<HTMLAnchorElement>('a[href="mailto:admin@restockroo.com.au"]');
      expect(mailto?.textContent?.trim()).toBe('admin@restockroo.com.au');
      expect(text(byTestId(footer as unknown as HTMLElement, 'landing-footer-contact'))).toContain('Eunhye Chung · ABN 69 235 469 430');

      for (const link of Array.from(footer.querySelectorAll('nav a'))) {
        expect(link.getAttribute('href')).toMatch(/^#(features|how-it-works|nayax|faqs)$/);
      }
    });

    it('has no Pricing link in the header, mobile menu or footer navigation', async () => {
      const { host } = await render();

      expect(host.querySelector('header a[href="#pricing"]')).toBeNull();
      expect(host.querySelector('[data-testid="landing-mobile-menu"] a[href="#pricing"]')).toBeNull();
      expect(host.querySelector('footer a[href="#pricing"]')).toBeNull();
    });
  });

  describe('refill example (issue #1192)', () => {
    it('names the fictional site and machine, labelled as an example', async () => {
      const { host } = await render();
      const refill = byTestId(host, 'landing-refill-example')!;

      expect(text(refill)).toContain('Banksia House · Banksia House VM');
      expect(text(host.querySelector('#landing-refill-label'))).toBe('Example refill · Fictional data');
    });

    it('shows a To pack and a Loaded quantity for each product, and a note that Loaded is not a separate record', async () => {
      const { host } = await render();

      const rows = Array.from(host.querySelectorAll('[data-testid="landing-refill-row"]')).map((row) => ({
        product: text(row.querySelector('th')),
        toPack: text(row.querySelectorAll('td')[0]).replace('Packed', '').trim(),
        loaded: text(row.querySelectorAll('td')[1]).replace('Packed', '').trim()
      }));
      expect(rows).toEqual([
        { product: 'Coke - Original 375mL', toPack: '24', loaded: '24' },
        { product: "Smith's Chips - Original 45g", toPack: '12', loaded: '8' },
        { product: 'Red Bull - Original 250mL', toPack: '10', loaded: '0' },
        { product: 'Kit Kat - 4 Finger 45g', toPack: '8', loaded: '8' }
      ]);

      const note = text(byTestId(host, 'landing-refill-loaded-note'));
      expect(note.toLowerCase()).toContain('not a separate record');
    });
  });

  describe('supplier comparison example (issue #1192)', () => {
    it('is headed "Compare prices across supported suppliers", with the example and confirm notices', async () => {
      const { host } = await render();
      const comparison = byTestId(host, 'landing-supplier-comparison')!;

      expect(text(comparison.querySelector('#landing-supplier-comparison-heading'))).toBe(
        'Compare prices across supported suppliers'
      );
      expect(text(byTestId(host, 'landing-supplier-example-notice'))).toBe('Example comparison — prices and availability change.');
      expect(text(byTestId(host, 'landing-supplier-confirm-notice'))).toContain(
        'Confirm the product, pack size and final price with the supplier before buying.'
      );
    });

    it('carries the rows the issue specifies, with Lowest unit price on every tied 30-pack row and nowhere else', async () => {
      const { host } = await render();
      // Scoped to the inline (desktop) comparison: the same rows are repeated inside the mobile dialog.
      const inlineComparison = byTestId(host, 'landing-supplier-comparison-inline')!;

      const rows = Array.from(inlineComparison.querySelectorAll('[data-testid="landing-supplier-row"]')).map(
        (row) => {
          const cells = row.querySelectorAll('td');
          return {
            supplier: text(cells[0]).replace('Lowest unit price', '').replace('Special', '').trim(),
            badges: Array.from(row.querySelectorAll('[data-testid="landing-supplier-badge"]')).map(text),
            packSize: text(cells[1]),
            packPrice: text(cells[2]),
            unitPrice: text(cells[3]),
            gstBasis: text(cells[4]),
            availability: text(cells[5]).replace('Online price, may differ in store', '').trim(),
            lastChecked: text(cells[6])
          };
        }
      );

      expect(rows).toEqual([
        {
          supplier: 'Coles',
          badges: ['Lowest unit price'],
          packSize: '30 pack',
          packPrice: '$32.00',
          unitPrice: '$1.07 inc GST / $0.97 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '11/11/2026, 05:17 am'
        },
        {
          supplier: 'Woolworths',
          badges: ['Lowest unit price', 'Special'],
          packSize: '30 pack',
          packPrice: '$32.00',
          unitPrice: '$1.07 inc GST / $0.97 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '11/11/2026, 05:17 am'
        },
        {
          supplier: 'Big W',
          badges: ['Lowest unit price'],
          packSize: '30 pack',
          packPrice: '$32.00',
          unitPrice: '$1.07 inc GST / $0.97 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '08/11/2026, 09:10 am'
        },
        {
          supplier: 'Coles',
          badges: ['Special'],
          packSize: '24 pack',
          packPrice: '$27.00',
          unitPrice: '$1.13 inc GST / $1.02 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '11/11/2026, 05:17 am'
        },
        {
          supplier: 'Big W',
          badges: [],
          packSize: '24 pack',
          packPrice: '$27.00',
          unitPrice: '$1.13 inc GST / $1.02 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '08/11/2026, 09:10 am'
        },
        {
          supplier: 'Woolworths',
          badges: ['Special'],
          packSize: '10 pack',
          packPrice: '$11.50',
          unitPrice: '$1.15 inc GST / $1.05 ex GST',
          gstBasis: 'inc GST',
          availability: 'In stock',
          lastChecked: '11/11/2026, 05:17 am'
        },
        {
          supplier: 'ALDI',
          badges: [],
          packSize: '18 pack',
          packPrice: '$20.99',
          unitPrice: '$1.17 inc GST / $1.06 ex GST',
          gstBasis: 'inc GST',
          availability: 'Stock unknown',
          lastChecked: '11/11/2026, 05:17 am'
        }
      ]);

      const onlineNotes = Array.from(inlineComparison.querySelectorAll('[data-testid="landing-supplier-online-note"]')).map(text);
      expect(onlineNotes).toEqual(Array(7).fill('Online price, may differ in store'));
    });

    it('wires the mobile preview button to open the full comparison in a dialog and returns focus on close', async () => {
      // The dialog's own Tab cycle, Escape and backdrop dismissal are covered in
      // landing-supplier-comparison-example.component.spec.ts; this is the page's wiring.
      const { host } = await render();
      document.body.appendChild(host);

      const trigger = byTestId<HTMLButtonElement>(host, 'landing-supplier-view-full-comparison')!;
      const dialog = byTestId<HTMLDialogElement>(host, 'landing-supplier-comparison-dialog')!;
      trigger.click();

      expect(dialog.open).toBe(true);
      expect(text(dialog.querySelector('h2'))).toBe('Compare prices across supported suppliers');
      expect(dialog.querySelectorAll('[data-testid="landing-supplier-row"]')).toHaveLength(7);

      dialog.querySelector<HTMLButtonElement>('button[aria-label="Close supplier comparison"]')!.click();

      expect(dialog.open).toBe(false);
      expect(document.activeElement).toBe(trigger);

      host.remove();
    });
  });

  describe('actions for a signed-out visitor', () => {
    it('offers Sign in and Start free, and no Open app', async () => {
      const { host } = await render();

      expect(byTestId(host, 'landing-sign-in')).not.toBeNull();
      expect(text(byTestId(host, 'landing-get-started'))).toBe('Start free');
      expect(text(byTestId(host, 'landing-primary-get-started'))).toBe('Start free');
      expect(byTestId(host, 'landing-open-app')).toBeNull();
    });

    it('starts the existing business application journey from Start free', async () => {
      const { host, navigateByUrl } = await render();

      byTestId(host, 'landing-primary-get-started')!.click();
      byTestId(host, 'landing-get-started')!.click();

      expect(navigateByUrl.mock.calls).toEqual([['/apply'], ['/apply']]);
    });

    it('redirects straight to the workforce sign-in, returning to the app entry route, with one authority', async () => {
      unconfigureExternalId();
      const { host, loginRedirect, navigateByUrl } = await render();

      byTestId(host, 'landing-sign-in')!.click();

      expect(loginRedirect).toHaveBeenCalledTimes(1);
      expect(loginRedirect.mock.calls[0][0]).toEqual(expect.objectContaining({ redirectStartPage: '/' }));
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('opens the sign-in chooser when a second authority is configured', async () => {
      configureExternalId();
      const { host, loginRedirect, navigateByUrl } = await render();

      byTestId(host, 'landing-sign-in')!.click();

      expect(navigateByUrl).toHaveBeenCalledWith('/sign-in');
      expect(loginRedirect).not.toHaveBeenCalled();
    });
  });

  describe('actions for a signed-in person', () => {
    it('offers Open app, to the app entry route, instead of Sign in and Start free', async () => {
      const { host } = await render({ signedIn: true });

      expect(byTestId(host, 'landing-open-app')?.getAttribute('href')).toBe('/');
      expect(byTestId(host, 'landing-primary-open-app')?.getAttribute('href')).toBe('/');
      expect(byTestId(host, 'landing-sign-in')).toBeNull();
      expect(byTestId(host, 'landing-get-started')).toBeNull();
      expect(byTestId(host, 'landing-primary-get-started')).toBeNull();
    });
  });

  describe('navigation within the page', () => {
    it('scrolls to the chosen section and moves focus to it', async () => {
      const { host } = await render();
      const section = host.querySelector<HTMLElement>('#faqs')!;
      const scrollIntoView = jest.fn();
      section.scrollIntoView = scrollIntoView;
      document.body.appendChild(host);

      const link = Array.from(host.querySelectorAll<HTMLAnchorElement>('header nav a')).find((a) => text(a) === 'FAQs')!;
      link.click();

      expect(scrollIntoView).toHaveBeenCalledWith(expect.objectContaining({ block: 'start' }));
      expect(document.activeElement).toBe(section);
      host.remove();
    });

    it('scrolls without animation for a visitor who prefers reduced motion', async () => {
      const original = window.matchMedia;
      window.matchMedia = ((query: string) => ({ matches: query.includes('reduce') })) as unknown as typeof window.matchMedia;
      try {
        const { host } = await render();
        const section = host.querySelector<HTMLElement>('#features')!;
        const scrollIntoView = jest.fn();
        section.scrollIntoView = scrollIntoView;
        document.body.appendChild(host);

        host.querySelector<HTMLAnchorElement>('main a[href="#features"]')!.click();

        expect(scrollIntoView).toHaveBeenCalledWith(expect.objectContaining({ behavior: 'auto' }));
        host.remove();
      } finally {
        window.matchMedia = original;
      }
    });

    it('offers a skip link that moves focus to the main content', async () => {
      const { host } = await render();
      document.body.appendChild(host);

      byTestId(host, 'landing-skip-link')!.click();

      expect(document.activeElement?.id).toBe('main-content');
      host.remove();
    });
  });

  describe('title and description', () => {
    it('publishes its own while shown and puts back what it found when left', async () => {
      document.title = 'Inventory Manager';
      const { fixture } = await render();
      const title = TestBed.inject(Title);
      const meta = TestBed.inject(Meta);

      expect(title.getTitle()).toBe(LANDING_TITLE);
      expect(meta.getTag('name="description"')?.content).toBe(LANDING_DESCRIPTION);

      fixture.destroy();

      expect(title.getTitle()).toBe('Inventory Manager');
      expect(meta.getTag('name="description"')).toBeNull();
    });
  });
});
