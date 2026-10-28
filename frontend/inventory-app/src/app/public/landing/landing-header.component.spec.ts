import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LandingHeaderComponent } from './landing-header.component';

interface Rendered {
  fixture: ComponentFixture<LandingHeaderComponent>;
  host: HTMLElement;
  menuButton: HTMLButtonElement;
  menu: HTMLElement;
}

async function render(isSignedIn = false): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [LandingHeaderComponent],
    providers: [provideRouter([])]
  }).compileComponents();

  const fixture = TestBed.createComponent(LandingHeaderComponent);
  fixture.componentInstance.isSignedIn = isSignedIn;
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  document.body.appendChild(host);
  return {
    fixture,
    host,
    menuButton: host.querySelector<HTMLButtonElement>('[data-testid="landing-menu-button"]')!,
    menu: host.querySelector<HTMLElement>('[data-testid="landing-mobile-menu"]')!
  };
}

function pressEscape(): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
}

/** The landing page's header and its narrow-layout menu (issue #954). */
describe('LandingHeaderComponent (issue #954)', () => {
  afterEach(() => {
    document.body.innerHTML = '';
    TestBed.resetTestingModule();
  });

  it('offers a native menu button that names the panel it controls and starts collapsed', async () => {
    const { menuButton, menu } = await render();

    expect(menuButton.tagName).toBe('BUTTON');
    expect(menuButton.getAttribute('type')).toBe('button');
    expect(menuButton.getAttribute('aria-controls')).toBe(menu.id);
    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
    expect(menuButton.getAttribute('aria-label')).toBe('Open menu');
    expect(menu.hidden).toBe(true);
  });

  it('opens and closes on the button, exposing its expanded state', async () => {
    const { fixture, menuButton, menu } = await render();

    menuButton.click();
    fixture.detectChanges();
    expect(menuButton.getAttribute('aria-expanded')).toBe('true');
    expect(menuButton.getAttribute('aria-label')).toBe('Close menu');
    expect(menu.hidden).toBe(false);

    menuButton.click();
    fixture.detectChanges();
    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
    expect(menu.hidden).toBe(true);
  });

  it('closes on Escape and returns focus to the menu button', async () => {
    const { fixture, menuButton, menu } = await render();
    menuButton.click();
    fixture.detectChanges();
    menu.querySelector<HTMLAnchorElement>('a')!.focus();

    pressEscape();
    fixture.detectChanges();

    expect(menu.hidden).toBe(true);
    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(menuButton);
  });

  it('leaves focus alone on Escape while the menu is closed', async () => {
    const { host } = await render();
    const brand = host.querySelector<HTMLAnchorElement>('[data-testid="landing-brand"]')!;
    brand.focus();

    pressEscape();

    expect(document.activeElement).toBe(brand);
  });

  it('reports the chosen section and closes after a link in it is selected', async () => {
    const { fixture, menuButton, menu } = await render();
    const requested: string[] = [];
    fixture.componentInstance.sectionRequested.subscribe((id) => requested.push(id));
    menuButton.click();
    fixture.detectChanges();

    const links = Array.from(menu.querySelectorAll<HTMLAnchorElement>('nav a'));
    expect(links.map((link) => [link.textContent?.trim(), link.getAttribute('href')])).toEqual([
      ['Features', '#features'],
      ['How it works', '#how-it-works'],
      ['Nayax integration', '#nayax'],
      ['FAQs', '#faqs']
    ]);

    links[2].click();
    fixture.detectChanges();

    expect(requested).toEqual(['nayax']);
    expect(menu.hidden).toBe(true);
    expect(menuButton.getAttribute('aria-expanded')).toBe('false');
  });

  it('closes the menu before reporting Sign in or Get started from it', async () => {
    const { fixture, host, menuButton, menu } = await render();
    let signIn = 0;
    let getStarted = 0;
    fixture.componentInstance.signInRequested.subscribe(() => signIn++);
    fixture.componentInstance.getStartedRequested.subscribe(() => getStarted++);

    menuButton.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="landing-menu-sign-in"]')!.click();
    fixture.detectChanges();
    expect(menu.hidden).toBe(true);

    menuButton.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="landing-menu-get-started"]')!.click();
    fixture.detectChanges();

    expect([signIn, getStarted]).toEqual([1, 1]);
    expect(menu.hidden).toBe(true);
  });

  it('offers only Open app, to the app entry route, in the menu of a signed-in person', async () => {
    const { host, menu } = await render(true);

    const actions = Array.from(menu.querySelectorAll('nav div a, nav div button')).map((element) => [
      element.textContent?.trim(),
      element.getAttribute('href')
    ]);
    expect(actions).toEqual([['Open app', '/']]);
    expect(host.querySelector('[data-testid="landing-sign-in"]')).toBeNull();
  });
});
