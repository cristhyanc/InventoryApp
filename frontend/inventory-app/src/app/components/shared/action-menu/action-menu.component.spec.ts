import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActionMenuComponent } from './action-menu.component';

@Component({
  standalone: true,
  imports: [ActionMenuComponent],
  template: `
    <button type="button" data-testid="outside">Outside</button>
    <app-action-menu label="More actions" icon="more_vert" testId="menu-a">
      <button type="button" role="menuitem" (click)="chosen = 'first'">First</button>
      <button type="button" role="menuitem" (click)="chosen = 'second'">Second</button>
      <button type="button" role="menuitem" (click)="chosen = 'third'">Third</button>
    </app-action-menu>
    <app-action-menu label="Columns" text="Columns" kind="panel" testId="menu-b">
      <label><input type="checkbox" data-testid="option" /> Option</label>
    </app-action-menu>
  `
})
class HostComponent {
  chosen = '';
}

/** The shared menu button and disclosure panel behind Columns, More actions and the row menus (issue #1093). */
describe('ActionMenuComponent (issue #1093)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    host = fixture.nativeElement as HTMLElement;
    document.body.appendChild(host);
  });

  afterEach(() => {
    host.remove();
    TestBed.resetTestingModule();
  });

  const trigger = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement;
  const panel = (id: string) => host.querySelector(`[data-testid="${id}-panel"]`) as HTMLElement | null;
  const key = (target: Element, name: string) => {
    target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };

  it('starts closed, with an accessible name on the icon-only trigger', () => {
    expect(panel('menu-a')).toBeNull();
    expect(panel('menu-b')).toBeNull();
    expect(trigger('menu-a').getAttribute('aria-label')).toBe('More actions');
    expect(trigger('menu-a').getAttribute('aria-haspopup')).toBe('menu');
    expect(trigger('menu-a').getAttribute('aria-expanded')).toBe('false');
    // A visible text label is the name; it is not repeated as aria-label.
    expect(trigger('menu-b').getAttribute('aria-label')).toBeNull();
    expect(trigger('menu-b').textContent?.trim()).toBe('Columns');
  });

  it('opens a labelled menu and focuses its first item', () => {
    trigger('menu-a').click();
    fixture.detectChanges();

    const menu = panel('menu-a')!;
    expect(menu.getAttribute('role')).toBe('menu');
    expect(menu.getAttribute('aria-labelledby')).toBe(trigger('menu-a').id);
    expect(trigger('menu-a').getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement?.textContent?.trim()).toBe('First');
  });

  it('moves between items with Arrow keys, Home and End, wrapping at the ends', () => {
    trigger('menu-a').click();
    fixture.detectChanges();
    const menu = panel('menu-a')!;

    key(menu, 'ArrowDown');
    expect(document.activeElement?.textContent?.trim()).toBe('Second');
    key(menu, 'End');
    expect(document.activeElement?.textContent?.trim()).toBe('Third');
    key(menu, 'ArrowDown');
    expect(document.activeElement?.textContent?.trim()).toBe('First');
    key(menu, 'ArrowUp');
    expect(document.activeElement?.textContent?.trim()).toBe('Third');
    key(menu, 'Home');
    expect(document.activeElement?.textContent?.trim()).toBe('First');
  });

  it('opens from Arrow Down on the trigger', () => {
    key(trigger('menu-a'), 'ArrowDown');

    expect(panel('menu-a')).not.toBeNull();
  });

  it('runs the chosen item, closes, and returns focus to the trigger', () => {
    trigger('menu-a').click();
    fixture.detectChanges();

    (Array.from(panel('menu-a')!.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Second') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.componentInstance.chosen).toBe('second');
    expect(panel('menu-a')).toBeNull();
    expect(document.activeElement).toBe(trigger('menu-a'));
  });

  it('closes on Escape with focus back on the trigger', () => {
    trigger('menu-a').click();
    fixture.detectChanges();

    key(panel('menu-a')!, 'Escape');

    expect(panel('menu-a')).toBeNull();
    expect(document.activeElement).toBe(trigger('menu-a'));
  });

  it('closes on a click outside it', () => {
    trigger('menu-a').click();
    fixture.detectChanges();

    trigger('outside').click();
    fixture.detectChanges();

    expect(panel('menu-a')).toBeNull();
  });

  it('keeps a panel open while its controls are used', () => {
    trigger('menu-b').click();
    fixture.detectChanges();

    const option = host.querySelector('[data-testid="option"]') as HTMLInputElement;
    expect(document.activeElement).toBe(option);
    expect(panel('menu-b')?.getAttribute('role')).toBe('group');
    option.click();
    fixture.detectChanges();

    expect(panel('menu-b')).not.toBeNull();
  });

  it('opens only one overlay at a time', () => {
    trigger('menu-a').click();
    fixture.detectChanges();
    trigger('menu-b').click();
    fixture.detectChanges();

    expect(panel('menu-a')).toBeNull();
    expect(panel('menu-b')).not.toBeNull();
  });
});
