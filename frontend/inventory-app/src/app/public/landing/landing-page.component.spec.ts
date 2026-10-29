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
    it('has one h1, carrying the approved headline', async () => {
      const { host } = await render();

      const headings = host.querySelectorAll('h1');
      expect(headings).toHaveLength(1);
      expect(text(headings[0])).toBe('Know your stock. Understand your costs.');
    });

    it('keeps the approved section order, with the section labels of the approved mobile design, plus pricing (issue #971)', async () => {
      const { host } = await render();

      const sectionIds = Array.from(host.querySelectorAll('main section[id]')).map((section) => section.id);
      expect(sectionIds).toEqual(['features', 'nayax', 'how-it-works', 'pricing', 'faqs']);

      const eyebrows = Array.from(host.querySelectorAll('main section p:first-child'))
        .map((label) => text(label))
        .filter((label) =>
          ['Practical inventory management', 'Features', 'Optional integration', 'How it works', 'Pricing', 'Frequently asked questions'].includes(
            label
          )
        );
      expect(eyebrows).toEqual([
        'Practical inventory management',
        'Features',
        'Optional integration',
        'How it works',
        'Pricing',
        'Frequently asked questions'
      ]);
    });

    it('uses "Stock levels" only as the example dashboard\'s stock list', async () => {
      const { host } = await render();

      const mentions = Array.from(host.querySelectorAll('h1, h2, h3, p, span')).filter((element) =>
        element.children.length === 0 && text(element) === 'Stock levels'
      );
      expect(mentions).toHaveLength(1);
      expect(mentions[0].closest('[data-testid="landing-dashboard"]')).not.toBeNull();
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

      expect(text(host.querySelector('#nayax h2'))).toBe('Core inventory works on its own. Connect Nayax when you need it.');
      expect(text(host.querySelector('#how-it-works'))).toContain(
        'Running vending machines? Connect Nayax for supported sales and reporting features.'
      );
      expect(text(host.querySelector('#faqs'))).toContain('No. Core inventory features work without connecting a sales provider.');
    });

    it('says Nayax will be a Pro feature once paid plans are live, without claiming that today (issue #971)', async () => {
      const { host } = await render();
      const nayaxSection = text(host.querySelector('#nayax'));

      expect(nayaxSection).toContain('Nayax will be a Pro plan feature once paid plans are live');
      expect(nayaxSection).toContain('Nayax will be a Pro feature once paid plans launch, and connecting it stays optional');
    });

    it('lists the three workflow steps in order', async () => {
      const { host } = await render();

      const steps = Array.from(host.querySelectorAll('[data-testid="landing-steps"] h3')).map(text);
      expect(steps).toEqual([
        'Step 01: Create your business',
        'Step 02: Add your products and opening stock',
        'Step 03: Record purchases and stock movements'
      ]);
    });

    it('answers the FAQs without claiming a native app, including the new pricing and storage questions (issue #971)', async () => {
      const { host } = await render();

      const questions = Array.from(host.querySelectorAll('[data-testid="landing-faqs"] h3')).map(text);
      expect(questions).toEqual([
        'Do I need Nayax?',
        'Is Restockroo suitable for vending businesses?',
        'Can I manage purchases and suppliers?',
        'Can I use Restockroo on my phone?',
        'When can I subscribe to Starter or Pro?',
        'What happens if I reach my storage limit?',
        'Will connecting Nayax cost extra?'
      ]);
      const faqText = text(host.querySelector('[data-testid="landing-faqs"]'));
      expect(faqText).toContain('There is no app to install.');
      expect(faqText).toContain('Starter and Pro are coming soon');
      expect(faqText).toContain("From then on you'll see a warning at 80% of your plan's attachment storage");
      expect(faqText).toContain('5 connected machines');
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
        'abn',
        'waitlist',
        'checkout'
      ]) {
        expect(page).not.toContain(claim);
      }
    });

    it('links no legal or contact page that does not exist, and dates the copyright this year', async () => {
      const { host } = await render();
      const footer = host.querySelector('footer')!;

      expect(text(footer)).not.toMatch(/privacy|terms|contact/i);
      expect(text(footer)).toContain(`© ${new Date().getFullYear()} Restockroo. All rights reserved.`);
      for (const link of Array.from(footer.querySelectorAll('a'))) {
        expect(link.getAttribute('href')).toMatch(/^#(features|how-it-works|nayax|pricing|faqs)$/);
      }
    });
  });

  describe('pricing (issue #971)', () => {
    it('shows Free, Starter and Pro with their approved prices and badges', async () => {
      const { host } = await render();

      const cards = Array.from(host.querySelectorAll('[data-testid="landing-plans"] > li'));
      expect(cards).toHaveLength(3);

      const summaries = cards.map((card) => ({
        name: text(card.querySelector('h3')),
        price: text(card.querySelector('.text-3xl')),
        badge: text(card.querySelector('span'))
      }));
      expect(summaries).toEqual([
        { name: 'Free', price: 'A$0', badge: 'Available now' },
        { name: 'Starter', price: 'A$7', badge: 'Coming soon' },
        { name: 'Pro', price: 'A$15', badge: 'Coming soon' }
      ]);
    });

    it('shows the 1 GB / 20 GB / 20 GB attachment allowances and the manual, comparison and report entitlements', async () => {
      const { host } = await render();

      expect(text(host.querySelector('[data-testid="landing-plan-free"]'))).toContain('1 GB of purchase and expense attachments');
      expect(text(host.querySelector('[data-testid="landing-plan-free"]'))).toContain('Manual Pick List entry');

      const starter = text(host.querySelector('[data-testid="landing-plan-starter"]'));
      expect(starter).toContain('20 GB of purchase and expense attachments');
      expect(starter).toContain('Daily cheapest-supplier comparisons');
      expect(starter).toContain('Bookkeeping and GST reports');

      const pro = text(host.querySelector('[data-testid="landing-plan-pro"]'));
      expect(pro).toContain('20 GB of purchase and expense attachments');
      expect(pro).toContain('Connect your Nayax account');
      expect(pro).toContain('5 connected machines included, then A$2/month per additional connected machine');
    });

    it("distinguishes Pro's plan entitlement from needing a usable Nayax connection", async () => {
      const { host } = await render();

      expect(text(host.querySelector('[data-testid="landing-plan-pro"]'))).toContain(
        'Plan entitlement and a usable Nayax connection are separate'
      );
    });

    it('sends every plan card through the existing business application journey, never a checkout', async () => {
      const { host, navigateByUrl } = await render();

      byTestId(host, 'landing-plan-cta-free')!.click();
      byTestId(host, 'landing-plan-cta-starter')!.click();
      byTestId(host, 'landing-plan-cta-pro')!.click();

      expect(navigateByUrl.mock.calls).toEqual([['/apply'], ['/apply'], ['/apply']]);
    });

    /**
     * Copy regression for the plan limits that have not shipped: attachment quota enforcement
     * (issue #968) and recording a purchase without its document (issue #976) are both still to
     * come, and current develop refuses a purchase with no file. The page may therefore describe
     * the allowances only as what happens once the paid plans launch, and must never present
     * either as today's behaviour.
     */
    it('presents the storage allowances as future behaviour, and promises no document-free purchase', async () => {
      const { host } = await render();

      expect(text(host.querySelector('#pricing'))).toContain(
        'The storage allowances and the connected-machine charge start when the paid plans launch - nothing is limited today.'
      );
      expect(text(byTestId(host, 'landing-plan-free'))).toContain(
        'Attachment storage is not limited today. The 1 GB allowance starts when the paid plans launch.'
      );

      const storage = Array.from(host.querySelectorAll('[data-testid="landing-faqs"] li')).find(
        (item) => text(item.querySelector('h3')) === 'What happens if I reach my storage limit?'
      );
      expect(text(storage?.querySelector('p'))).toBe(
        'Nothing is limited today: attachment storage is unrestricted while Free is the only plan, and the allowances above ' +
          "start when the paid plans launch. From then on you'll see a warning at 80% of your plan's attachment storage, and " +
          'at 100% new uploads will pause until you free up space or upgrade, while existing attachments stay available to ' +
          'view, download and delete.'
      );

      // Nothing anywhere on the page may say a limit, a paused upload or a per-machine charge
      // applies now, or that a purchase or expense can be recorded with no document attached.
      const page = text(host);
      expect(page).not.toMatch(/without attaching/i);
      expect(page).not.toMatch(/uploads pause/i);
      expect(page).not.toMatch(/are A\$2\/month/i);
    });

    /**
     * Keyboard reachability of the new section, as far as jsdom can carry it: every plan card's
     * action is a native control, in card order, with no `tabindex` and no disabled state of its
     * own, so it stays in the page's natural tab sequence and takes the shared `:focus-visible`
     * outline. Rendered-layout and focus-ring checks at 360/390/768/1440px still need a browser.
     */
    it('makes every plan card action a native, keyboard-reachable control in card order', async () => {
      const { host } = await render();

      const actions = Array.from(
        host.querySelectorAll<HTMLElement>('[data-testid="landing-plans"] [data-testid^="landing-plan-cta-"]')
      );
      expect(actions.map((action) => action.dataset['testid'])).toEqual([
        'landing-plan-cta-free',
        'landing-plan-cta-starter',
        'landing-plan-cta-pro'
      ]);
      for (const action of actions) {
        expect(action.tagName).toBe('BUTTON');
        expect(action.getAttribute('type')).toBe('button');
        expect(action.hasAttribute('tabindex')).toBe(false);
        expect(action.hasAttribute('disabled')).toBe(false);
      }
    });

    it('offers Open app from every plan card to a signed-in person, instead of Get started', async () => {
      const { host } = await render({ signedIn: true });

      for (const id of ['free', 'starter', 'pro']) {
        const cta = byTestId<HTMLAnchorElement>(host, `landing-plan-cta-${id}`)!;
        expect(text(cta)).toBe('Open app');
        expect(cta.getAttribute('href')).toBe('/');
      }
    });
  });

  describe('example dashboard', () => {
    it('is labelled as sample data', async () => {
      const { host } = await render();
      const dashboard = byTestId(host, 'landing-dashboard')!;

      expect(dashboard.getAttribute('aria-labelledby')).toBe('landing-dashboard-label');
      expect(text(host.querySelector('#landing-dashboard-label'))).toBe('Example dashboard · Sample data');
    });

    it('shows the approved sample figures', async () => {
      const { host } = await render();
      const dashboard = text(byTestId(host, 'landing-dashboard'));

      expect(dashboard).toContain('922 items');
      expect(dashboard).toContain('2 products');
      expect(dashboard).toContain('11 dockets');
      expect(dashboard).toContain('$1,840.50');
      expect(dashboard).toContain('$2,482.83');
    });

    it('shows the three stock levels, with low-stock warnings on Red Bull and Kinder Bueno only', async () => {
      const { host } = await render();

      const rows = Array.from(host.querySelectorAll('[data-testid="landing-stock-table"] tbody tr')).map((row) => ({
        product: text(row.querySelector('th')).replace(' Low stock', ''),
        inStock: text(row.querySelectorAll('td')[0]),
        minimum: text(row.querySelectorAll('td')[1]),
        warned: row.querySelector('[data-testid="landing-low-stock"]') !== null
      }));
      expect(rows).toEqual([
        { product: 'Red Bull 250mL', inStock: '0', minimum: '7', warned: true },
        { product: 'Coke 375mL', inStock: '26', minimum: '17', warned: false },
        { product: 'Kinder Bueno', inStock: '5', minimum: '7', warned: true }
      ]);

      const listWarnings = Array.from(host.querySelectorAll('[data-testid="landing-stock-list"] li'))
        .filter((item) => item.querySelector('[data-testid="landing-low-stock"]'))
        .map((item) => text(item.querySelector('.font-semibold')));
      expect(listWarnings).toEqual(['Red Bull 250mL', 'Kinder Bueno']);
    });
  });

  describe('actions for a signed-out visitor', () => {
    it('offers Sign in and Get started, and no Open app', async () => {
      const { host } = await render();

      expect(byTestId(host, 'landing-sign-in')).not.toBeNull();
      expect(byTestId(host, 'landing-get-started')).not.toBeNull();
      expect(byTestId(host, 'landing-primary-get-started')).not.toBeNull();
      expect(byTestId(host, 'landing-open-app')).toBeNull();
    });

    it('starts the existing business application journey from Get started', async () => {
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
    it('offers Open app, to the app entry route, instead of Sign in and Get started', async () => {
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

    it('scrolls to Pricing and moves focus to it from the header link (issue #971)', async () => {
      const { host } = await render();
      const section = host.querySelector<HTMLElement>('#pricing')!;
      const scrollIntoView = jest.fn();
      section.scrollIntoView = scrollIntoView;
      document.body.appendChild(host);

      const link = Array.from(host.querySelectorAll<HTMLAnchorElement>('header nav a')).find((a) => text(a) === 'Pricing')!;
      link.click();

      expect(scrollIntoView).toHaveBeenCalledWith(expect.objectContaining({ block: 'start' }));
      expect(section.getAttribute('tabindex')).toBe('-1');
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
