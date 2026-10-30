import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HelpToggletipComponent } from './help-toggletip.component';

@Component({
  standalone: true,
  imports: [HelpToggletipComponent],
  template: `
    <app-help-toggletip label="unit price" text="The price of one unit." />
    <app-help-toggletip label="GST" text="Prices include GST." />
    <button type="button" id="elsewhere">Elsewhere</button>
  `
})
class HostComponent {}

describe('HelpToggletipComponent (issue #1029)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
  });

  const triggers = () => Array.from(host.querySelectorAll<HTMLButtonElement>('[data-testid="help-toggletip-trigger"]'));
  const panels = () => Array.from(host.querySelectorAll<HTMLElement>('[data-testid="help-toggletip-panel"]'));

  function click(element: HTMLElement): void {
    element.click();
    fixture.detectChanges();
  }

  it('is a labelled button that starts closed', () => {
    const [trigger] = triggers();
    expect(trigger.getAttribute('aria-label')).toBe('About unit price');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(trigger.type).toBe('button');
    expect(panels()).toHaveLength(0);
  });

  it('opens on click or tap, inside a status region, and closes on a second click', () => {
    const [trigger] = triggers();

    click(trigger);
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(panels()).toHaveLength(1);
    expect(panels()[0].textContent).toContain('The price of one unit.');
    expect(panels()[0].closest('[role="status"]')).not.toBeNull();
    expect(trigger.getAttribute('aria-controls')).toBe(panels()[0].id);

    click(trigger);
    expect(panels()).toHaveLength(0);
  });

  it('closes on Escape and gives focus back to its button', () => {
    const [trigger] = triggers();
    click(trigger);
    (host.querySelector('#elsewhere') as HTMLButtonElement).focus();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(panels()).toHaveLength(0);
    expect(document.activeElement).toBe(trigger);
  });

  it('closes when the pointer goes anywhere else', () => {
    click(triggers()[0]);

    click(host.querySelector('#elsewhere') as HTMLElement);

    expect(panels()).toHaveLength(0);
  });

  it('keeps only one tip open at a time', () => {
    const [first, second] = triggers();
    click(first);
    click(second);

    expect(panels()).toHaveLength(1);
    expect(panels()[0].textContent).toContain('Prices include GST.');
    expect(first.getAttribute('aria-expanded')).toBe('false');
  });

  it('places the panel inside the window, so nothing around it can clip it', () => {
    Object.defineProperty(window, 'innerWidth', { value: 360, configurable: true });
    const [trigger] = triggers();
    jest.spyOn(trigger, 'getBoundingClientRect').mockReturnValue(
      { left: 340, right: 356, top: 100, bottom: 116, width: 16, height: 16, x: 340, y: 100, toJSON: () => ({}) } as DOMRect);

    click(trigger);

    const panel = panels()[0];
    const left = parseFloat(panel.style.left);
    const width = parseFloat(panel.style.width);
    expect(left).toBeGreaterThanOrEqual(16);
    expect(left + width).toBeLessThanOrEqual(360 - 16);
    expect(parseFloat(panel.style.top)).toBe(122);
  });
});
