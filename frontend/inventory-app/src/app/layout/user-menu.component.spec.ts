import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { UserMenuComponent } from './user-menu.component';

interface Rendered {
  fixture: ComponentFixture<UserMenuComponent>;
  component: UserMenuComponent;
  host: HTMLElement;
}

async function render(options: { isSignedIn: boolean; userName?: string }): Promise<Rendered> {
  await TestBed.configureTestingModule({ imports: [UserMenuComponent] }).compileComponents();

  const fixture = TestBed.createComponent(UserMenuComponent);
  fixture.componentRef.setInput('isSignedIn', options.isSignedIn);
  fixture.componentRef.setInput('userName', options.userName ?? '');
  fixture.detectChanges();

  return { fixture, component: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
}

function button(host: HTMLElement, text: string): HTMLButtonElement | undefined {
  return Array.from(host.querySelectorAll('button')).find((candidate) => (candidate.textContent ?? '').includes(text));
}

describe('UserMenuComponent signed out (issue #391)', () => {
  it('offers a sign-in control and no account menu', async () => {
    const { component, host } = await render({ isSignedIn: false });
    const signInRequested = jest.fn();
    component.signInRequested.subscribe(signInRequested);

    const signIn = button(host, 'Sign in');
    expect(signIn).toBeDefined();
    expect(button(host, 'Sign out')).toBeUndefined();
    expect(host.querySelector('[aria-haspopup]')).toBeNull();

    signIn?.click();
    expect(signInRequested).toHaveBeenCalledTimes(1);
  });
});

describe('UserMenuComponent signed in (issue #391)', () => {
  it('shows the authenticated identity on a closed popover trigger', async () => {
    const { host } = await render({ isSignedIn: true, userName: 'Dana Operator' });

    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup]');
    expect(trigger).not.toBeNull();
    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
    expect(trigger?.textContent).toContain('Dana Operator');
    expect(button(host, 'Sign out')).toBeUndefined();
  });

  it('opens the popover and signs out from it', async () => {
    const { fixture, component, host } = await render({ isSignedIn: true, userName: 'Dana Operator' });
    const signOutRequested = jest.fn();
    component.signOutRequested.subscribe(signOutRequested);

    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup]');
    trigger?.click();
    fixture.detectChanges();

    expect(trigger?.getAttribute('aria-expanded')).toBe('true');
    expect(host.querySelector(`#${trigger?.getAttribute('aria-controls')}`)).not.toBeNull();

    const signOut = button(host, 'Sign out');
    expect(signOut).toBeDefined();
    signOut?.click();
    fixture.detectChanges();

    expect(signOutRequested).toHaveBeenCalledTimes(1);
    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
  });

  it('invents no profile or account destination in the popover', async () => {
    const { fixture, host } = await render({ isSignedIn: true, userName: 'Dana Operator' });
    host.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();

    // The two published legal documents (issue #1259) and nothing else: no profile page, no
    // account page, no destination this application does not have.
    expect(Array.from(host.querySelectorAll('a')).map((link) => (link.textContent ?? '').trim())).toEqual([
      'Terms of Use',
      'Privacy Policy'
    ]);
    expect(Array.from(host.querySelectorAll('button')).map((candidate) => (candidate.textContent ?? '').trim())).toEqual([
      expect.stringContaining('Dana Operator'),
      'Sign out'
    ]);
  });

  /**
   * Issue #1259: every signed-in page has to link the published Terms of Use and Privacy Policy,
   * and this menu is the one control on all of them. Both open in a new tab, so reading them never
   * costs whatever the person was in the middle of.
   */
  it('links the Terms of Use and the Privacy Policy, each opening in a new tab', async () => {
    const { fixture, host } = await render({ isSignedIn: true, userName: 'Dana Operator' });
    host.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();

    for (const [testId, href] of [
      ['user-menu-terms-of-use', '/terms-of-use'],
      ['user-menu-privacy-policy', '/privacy-policy']
    ]) {
      const link = host.querySelector(`[data-testid="${testId}"]`) as HTMLAnchorElement;
      expect(link.getAttribute('href')).toBe(href);
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toContain('noopener');
    }
  });

  it('offers neither legal link while nobody is signed in, where the public footer carries them', async () => {
    const { host } = await render({ isSignedIn: false });

    expect(host.querySelector('[data-testid="user-menu-terms-of-use"]')).toBeNull();
    expect(host.querySelector('[data-testid="user-menu-privacy-policy"]')).toBeNull();
  });

  it('closes the popover on Escape and returns focus to its trigger', async () => {
    const { fixture, host } = await render({ isSignedIn: true, userName: 'Dana Operator' });
    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup]');
    trigger?.click();
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
  });

  it('closes the popover when a click lands outside it', async () => {
    const { fixture, host } = await render({ isSignedIn: true, userName: 'Dana Operator' });
    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup]');
    trigger?.click();
    fixture.detectChanges();
    expect(trigger?.getAttribute('aria-expanded')).toBe('true');

    document.body.click();
    fixture.detectChanges();

    expect(trigger?.getAttribute('aria-expanded')).toBe('false');
  });

  it('falls back to a generic signed-in label when the account has no display name', async () => {
    const { host } = await render({ isSignedIn: true, userName: '' });

    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup]');
    expect(trigger).not.toBeNull();
    expect(trigger?.textContent?.trim()).toBeTruthy();
  });
});

/**
 * What the shell composes into the panel (issue #721): the **View as** entry is projected rather
 * than built here, so this component still owns no access decision, and it is closed through
 * {@link UserMenuComponent.close} because a click on projected content is a click *inside* this
 * element and would otherwise leave the panel open over a rebuilt page.
 */
describe('UserMenuComponent projected content (issue #721)', () => {
  @Component({ standalone: true, imports: [UserMenuComponent], template: '<app-user-menu [isSignedIn]="true"><button type="button">View as Operator</button></app-user-menu>' })
  class HostComponent {}

  afterEach(() => TestBed.resetTestingModule());

  it('renders projected content only while the panel is open', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).not.toContain('View as Operator');

    host.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();

    expect(host.querySelector('#user-menu-panel')?.textContent).toContain('View as Operator');
  });

  it('closes the panel when asked to, which a click inside it never does', async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector<HTMLButtonElement>('button[aria-haspopup]')?.click();
    fixture.detectChanges();

    const projected = Array.from(host.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === 'View as Operator'
    );
    projected?.click();
    fixture.detectChanges();
    expect(host.querySelector('#user-menu-panel')).not.toBeNull();

    fixture.debugElement.children[0].componentInstance.close();
    fixture.detectChanges();

    expect(host.querySelector('#user-menu-panel')).toBeNull();
  });
});
