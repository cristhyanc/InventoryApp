import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LandingSupplierComparisonExampleComponent } from './landing-supplier-comparison-example.component';

/**
 * The accessible dialog behaviour of the landing page's supplier comparison example (issue #1192):
 * opened with `showModal()`, its own Tab cycle (jsdom implements none of the platform's focus
 * trap, see `setup-jest.ts`), Escape, the backdrop and its Close controls, each returning focus to
 * the button that opened it. The rows and copy are covered in `landing-page.component.spec.ts`.
 */
const dialogPrototype = Object.getPrototypeOf(document.createElement('dialog')) as HTMLDialogElement;

/** A Tab, Shift+Tab or Escape press on whatever currently has focus, as the browser reports one. */
function press(key: string, shiftKey = false): void {
  (document.activeElement ?? document.body).dispatchEvent(
    new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true })
  );
}

describe('LandingSupplierComparisonExampleComponent (issue #1192)', () => {
  let showModal: jest.SpyInstance;
  let fixture: ComponentFixture<LandingSupplierComparisonExampleComponent>;
  let host: HTMLElement;
  let background: HTMLButtonElement;

  beforeEach(async () => {
    showModal = jest.spyOn(dialogPrototype, 'showModal');
    await TestBed.configureTestingModule({ imports: [LandingSupplierComparisonExampleComponent] }).compileComponents();
    fixture = TestBed.createComponent(LandingSupplierComparisonExampleComponent);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);

    background = document.createElement('button');
    background.textContent = 'A control on the page behind the dialog';
    document.body.appendChild(background);
  });

  afterEach(() => {
    jest.restoreAllMocks();
    host.remove();
    background.remove();
    TestBed.resetTestingModule();
  });

  function trigger(): HTMLButtonElement {
    return host.querySelector('[data-testid="landing-supplier-view-full-comparison"]') as HTMLButtonElement;
  }

  function dialog(): HTMLDialogElement {
    return host.querySelector('[data-testid="landing-supplier-comparison-dialog"]') as HTMLDialogElement;
  }

  function open(): HTMLDialogElement {
    trigger().click();
    fixture.detectChanges();
    return dialog();
  }

  function tabStops(): HTMLElement[] {
    return Array.from(dialog().querySelectorAll<HTMLElement>('button:not([disabled])'));
  }

  it('opens the full comparison as a native modal dialog, focused, from the preview button', () => {
    const panel = open();

    expect(panel.tagName).toBe('DIALOG');
    expect(showModal).toHaveBeenCalledTimes(1);
    expect(panel.open).toBe(true);
    expect(document.activeElement).toBe(panel);
    expect(panel.querySelectorAll('[data-testid="landing-supplier-row"]')).toHaveLength(7);
  });

  it('closes on Escape and returns focus to the button that opened it', () => {
    open();

    press('Escape');
    fixture.detectChanges();

    expect(dialog().open).toBe(false);
    expect(document.activeElement).toBe(trigger());
  });

  it('closes from its own Close button and returns focus to the trigger', () => {
    open();

    Array.from(dialog().querySelectorAll('button'))
      .find((button) => button.textContent?.trim() === 'Close')!
      .click();
    fixture.detectChanges();

    expect(dialog().open).toBe(false);
    expect(document.activeElement).toBe(trigger());
  });

  it('closes on a click beside the panel and not on one inside it, returning focus either way', () => {
    const panel = open();

    // A click on the content is not a dismissal: the `::backdrop` click is the one reported with
    // the dialog itself as its target.
    panel.querySelector('table')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();
    expect(panel.open).toBe(true);

    panel.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();

    expect(panel.open).toBe(false);
    expect(document.activeElement).toBe(trigger());
  });

  describe('keeps focus inside the open dialog', () => {
    it('moves Tab from the panel itself to its first control, not into the page behind it', () => {
      open();
      expect(document.activeElement).not.toBe(background);

      press('Tab');

      expect(document.activeElement).toBe(tabStops()[0]);
    });

    it('wraps Tab from the last control back to the first', () => {
      const panel = open();
      const stops = tabStops();
      expect(stops.length).toBeGreaterThan(1);
      stops[stops.length - 1].focus();

      press('Tab');

      expect(document.activeElement).toBe(stops[0]);
      expect(panel.contains(document.activeElement)).toBe(true);
    });

    it('wraps Shift+Tab from the first control back to the last, and from the panel to the last', () => {
      open();
      const stops = tabStops();
      const last = stops[stops.length - 1];

      press('Tab', true);
      expect(document.activeElement).toBe(last);

      stops[0].focus();
      press('Tab', true);
      expect(document.activeElement).toBe(last);
    });

    it('never lands on a control outside the dialog, Tab or Shift+Tab, however long it is held', () => {
      const panel = open();
      const stops = tabStops();
      const visited: HTMLElement[] = [];

      for (let step = 0; step < stops.length * 2 + 1; step += 1) {
        press('Tab');
        expect(document.activeElement).not.toBe(background);
        expect(panel.contains(document.activeElement)).toBe(true);
        visited.push(document.activeElement as HTMLElement);
      }

      expect(new Set(visited)).toEqual(new Set(stops));

      for (let step = 0; step < stops.length * 2 + 1; step += 1) {
        press('Tab', true);
        expect(document.activeElement).not.toBe(background);
        expect(panel.contains(document.activeElement)).toBe(true);
      }
    });
  });
});
