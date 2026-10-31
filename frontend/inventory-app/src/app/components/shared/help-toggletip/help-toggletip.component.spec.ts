import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HelpToggletipComponent } from './help-toggletip.component';

/**
 * The shared help toggletip (issues #1015 and #1016, the owner's decision of 31 October 2026 that
 * both build it once). These tests are the interaction the two issues describe: an icon-only button
 * with an accessible name, `aria-expanded`/`aria-controls`, click/tap, Enter, Space, hover, keyboard
 * focus, Escape and outside click, with focus returned to the button.
 *
 * What is deliberately not asserted here is the platform's half. The panel is rendered into the top
 * layer with the HTML `popover` attribute so no `overflow` on a card or a scrolling table can clip
 * it, and it is placed from the button's own rectangle; jsdom implements neither the top layer nor
 * layout, so a spec here could only assert the stub. The not-clipped, inside-the-viewport result is
 * the browser's and is captured by the Playwright visual evidence instead.
 */
@Component({
  standalone: true,
  imports: [HelpToggletipComponent],
  template: `
    <p>
      Cheapest supplier<app-help-toggletip label="About Cheapest supplier">{{ first }}</app-help-toggletip>
    </p>
    <p>
      Cheapest supplier<app-help-toggletip label="About Cheapest supplier">{{ second }}</app-help-toggletip>
    </p>
    <button type="button" id="elsewhere">Elsewhere</button>
  `
})
class HostComponent {
  readonly first = 'The first sentence.';
  readonly second = 'The second sentence.';
}

async function render() {
  await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();

  const fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();

  const host = fixture.nativeElement as HTMLElement;
  const buttons = () => Array.from(host.querySelectorAll<HTMLButtonElement>('button[aria-expanded]'));
  const button = (index = 0) => buttons()[index];
  const panels = () => Array.from(host.querySelectorAll<HTMLElement>('[data-testid="help-toggletip-panel"]'));
  const panel = (index = 0) => panels()[index] as HTMLElement | undefined;
  const click = (index = 0) => {
    button(index).click();
    fixture.detectChanges();
  };
  const press = (key: string, target: EventTarget = document) => {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };
  // `mouseenter` and `mouseleave` do not bubble: a browser dispatches them to the entered element
  // and to each of its ancestors, and the toggletip answers them on the wrapper around the button
  // and the panel, so that moving the pointer onto the panel is not "leaving" it.
  const wrapper = (index = 0) => button(index).parentElement as HTMLElement;
  const hover = (index = 0) => {
    wrapper(index).dispatchEvent(new MouseEvent('mouseenter'));
    fixture.detectChanges();
  };
  const unhover = (index = 0) => {
    wrapper(index).dispatchEvent(new MouseEvent('mouseleave'));
    fixture.detectChanges();
  };

  return { fixture, host, buttons, button, panels, panel, click, press, hover, unhover };
}

afterEach(() => TestBed.resetTestingModule());

describe('HelpToggletipComponent button', () => {
  it('is an icon-only button whose accessible name is the given label', async () => {
    const { button } = await render();

    expect(button().getAttribute('aria-label')).toBe('About Cheapest supplier');
    expect(button().textContent?.trim()).toBe('');
    expect(button().querySelector('svg')).not.toBeNull();
    expect(button().getAttribute('title')).toBeNull();
  });

  it('adds no text of its own to the label it sits beside', async () => {
    const { host } = await render();

    expect(host.querySelector('p')?.textContent?.trim()).toBe('Cheapest supplier');
  });

  it('reports the closed panel it controls with aria-expanded and aria-controls', async () => {
    const { button, panel } = await render();

    expect(button().getAttribute('aria-expanded')).toBe('false');
    expect(button().getAttribute('aria-controls')).toBeTruthy();
    expect(panel()).toBeUndefined();
  });

  it('gives each instance its own panel id, so a table of them has no duplicate id', async () => {
    const { buttons } = await render();
    const ids = buttons().map((candidate) => candidate.getAttribute('aria-controls'));

    expect(ids[0]).not.toBe(ids[1]);
    expect(new Set(ids).size).toBe(ids.length);
  });
});

describe('HelpToggletipComponent opening and closing', () => {
  it('opens the panel with the projected sentence on click, and says so', async () => {
    const { button, panel, click } = await render();

    click();

    expect(panel()?.textContent?.trim()).toBe('The first sentence.');
    expect(panel()?.id).toBe(button().getAttribute('aria-controls'));
    expect(button().getAttribute('aria-expanded')).toBe('true');
  });

  it('closes again on a second click', async () => {
    const { button, panel, click } = await render();

    click();
    click();

    expect(panel()).toBeUndefined();
    expect(button().getAttribute('aria-expanded')).toBe('false');
  });

  it('opens on Enter', async () => {
    const { button, panel, press } = await render();

    press('Enter', button());

    expect(panel()).not.toBeUndefined();
    expect(button().getAttribute('aria-expanded')).toBe('true');
  });

  it('opens on Space', async () => {
    const { button, panel, press } = await render();

    press(' ', button());

    expect(panel()).not.toBeUndefined();
  });

  it('closes on Enter and on Space again, so the keyboard toggles it as the pointer does', async () => {
    const { button, panel, press } = await render();

    press('Enter', button());
    press('Enter', button());
    expect(panel()).toBeUndefined();

    press(' ', button());
    press(' ', button());
    expect(panel()).toBeUndefined();
  });

  it('answers Enter and Space itself, so the browser does not activate the button a second time', async () => {
    const { button } = await render();
    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    const space = new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true });

    button().dispatchEvent(enter);
    button().dispatchEvent(space);

    expect(enter.defaultPrevented).toBe(true);
    expect(space.defaultPrevented).toBe(true);
  });

  it('opens on hover and closes again when the pointer leaves', async () => {
    const { panel, hover, unhover } = await render();

    hover();
    expect(panel()?.textContent?.trim()).toBe('The first sentence.');

    unhover();
    expect(panel()).toBeUndefined();
  });

  it('keeps a clicked panel open when the pointer leaves, because it was not only hovered', async () => {
    const { panel, click, unhover } = await render();

    click();
    unhover();

    expect(panel()).not.toBeUndefined();
  });

  it('opens on keyboard focus and closes when focus moves away', async () => {
    const { host, button, panel, fixture } = await render();

    button().dispatchEvent(new FocusEvent('focus', { bubbles: false }));
    fixture.detectChanges();
    expect(panel()).not.toBeUndefined();

    button().dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: host.querySelector('#elsewhere') }));
    fixture.detectChanges();
    expect(panel()).toBeUndefined();
  });

  it('opens only one panel at a time, so one card never explains another', async () => {
    const { panels, buttons, click } = await render();

    click(0);
    click(1);

    expect(panels()).toHaveLength(1);
    expect(buttons().map((candidate) => candidate.getAttribute('aria-expanded'))).toEqual(['false', 'true']);
  });
});

describe('HelpToggletipComponent dismissal', () => {
  it('closes on Escape and returns focus to its button', async () => {
    const { button, panel, click, press } = await render();

    button().focus();
    click();
    press('Escape');

    expect(panel()).toBeUndefined();
    expect(button().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button());
  });

  it('closes on a click outside it', async () => {
    const { host, panel, click, fixture } = await render();

    click();
    host.querySelector<HTMLButtonElement>('#elsewhere')?.click();
    fixture.detectChanges();

    expect(panel()).toBeUndefined();
  });

  it('stays open when the panel itself is clicked', async () => {
    const { panel, click, fixture } = await render();

    click();
    panel()?.click();
    fixture.detectChanges();

    expect(panel()).not.toBeUndefined();
  });

  it('stops listening to the document once it is destroyed, panel open or not', async () => {
    const { fixture, button, click } = await render();
    const focus = jest.spyOn(button(), 'focus');

    click();
    fixture.destroy();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));

    expect(focus).not.toHaveBeenCalled();
  });
});
