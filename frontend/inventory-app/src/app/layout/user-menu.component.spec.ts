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

    expect(host.querySelectorAll('a')).toHaveLength(0);
    expect(Array.from(host.querySelectorAll('button')).map((candidate) => (candidate.textContent ?? '').trim())).toEqual([
      expect.stringContaining('Dana Operator'),
      'Sign out'
    ]);
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
