import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { MemberInvitesComponent } from './member-invites.component';
import { CreatedMemberInvite, MemberInvite, MemberInviteService } from '../../../services/member-invite.service';
import { ToastService } from '../../../services/toast.service';

function invite(overrides: Partial<MemberInvite> = {}): MemberInvite {
  return {
    inviteId: 1,
    role: 'Manager',
    note: 'New site manager',
    status: 'Pending',
    createdAtUtc: '2026-01-01T00:00:00Z',
    expiresAtUtc: '2026-01-08T00:00:00Z',
    usedAtUtc: null,
    cancelledAtUtc: null,
    redeemedByMembershipId: null,
    ...overrides
  };
}

async function render(inviteService: Partial<MemberInviteService>, toast?: Partial<ToastService>) {
  await TestBed.configureTestingModule({
    imports: [MemberInvitesComponent],
    providers: [
      { provide: MemberInviteService, useValue: inviteService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(MemberInvitesComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

/**
 * The Invites section of the Admin → Users page (issue #534), over issue #531's three endpoints.
 */
describe('MemberInvitesComponent (issue #534)', () => {
  afterEach(() => {
    Object.defineProperty(window.navigator, 'clipboard', { value: undefined, configurable: true });
  });

  it('loads and lists every invite', async () => {
    const list = jest.fn(() => of([invite()]));
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('New site manager');
    expect(host.textContent).toContain('Pending');
  });

  it('shows an error state when the list fails to load', async () => {
    const { host } = await render({ list: () => throwError(() => new Error('boom')) });

    expect(host.textContent).toContain('Unable to load invites.');
  });

  it('shows an empty state when there are no invites', async () => {
    const { host } = await render({ list: () => of([]) });

    expect(host.textContent).toContain('No invites have been created.');
  });

  it('refuses to create an invite with no role selected, without calling the API', async () => {
    const create = jest.fn();
    const error = jest.fn();
    const { fixture, host } = await render({ list: () => of([]), create }, { error });

    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();

    expect(create).not.toHaveBeenCalled();
    expect(error).toHaveBeenCalledWith('Select a role for the invite.');
  });

  it('creates an invite, shows the link exactly once with its expiry, and prepends it to the list', async () => {
    const created: CreatedMemberInvite = { token: 'tok_abc123', invite: invite() };
    const create = jest.fn(() => of(created));
    const { fixture, host } = await render({ list: () => of([]), create });

    fixture.componentInstance.createRole = 'Manager';
    fixture.componentInstance.createNote = 'New site manager';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith('Manager', 'New site manager');
    const linkField = host.querySelector('[data-testid="invite-link"]') as HTMLInputElement;
    expect(linkField.value).toContain('tok_abc123');
    expect(host.textContent).toContain('will not be shown again');
    expect(host.textContent).toContain('New site manager');
  });

  it('sends a null note when none was entered', async () => {
    const created: CreatedMemberInvite = { token: 'tok_abc123', invite: invite({ note: null }) };
    const create = jest.fn(() => of(created));
    const { fixture, host } = await render({ list: () => of([]), create });

    fixture.componentInstance.createRole = 'Operator';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();

    expect(create).toHaveBeenCalledWith('Operator', null);
  });

  it('shows the API refusal verbatim when creation is refused', async () => {
    const error = jest.fn();
    const create = jest.fn(() => throwError(() => ({ error: { message: 'An invite note must be no longer than 200 characters.' } })));
    const { fixture, host } = await render({ list: () => of([]), create }, { error });

    fixture.componentInstance.createRole = 'Manager';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith('An invite note must be no longer than 200 characters.');
  });

  it('copies the created link to the clipboard when one is available', async () => {
    const writeText = jest.fn(() => Promise.resolve());
    Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true });
    const success = jest.fn();
    const created: CreatedMemberInvite = { token: 'tok_abc123', invite: invite() };
    const { fixture, host } = await render({ list: () => of([]), create: () => of(created) }, { success });

    fixture.componentInstance.createRole = 'Manager';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="copy-invite-link"]')?.click();
    await Promise.resolve();

    expect(writeText).toHaveBeenCalledWith(fixture.componentInstance.justCreated?.link);
    expect(success).toHaveBeenCalled();
  });

  it('offers manual selection when the browser has no clipboard', async () => {
    Object.defineProperty(window.navigator, 'clipboard', { value: undefined, configurable: true });
    const info = jest.fn();
    const created: CreatedMemberInvite = { token: 'tok_abc123', invite: invite() };
    const { fixture, host } = await render({ list: () => of([]), create: () => of(created) }, { info });

    fixture.componentInstance.createRole = 'Manager';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="copy-invite-link"]')?.click();

    expect(info).toHaveBeenCalled();
  });

  it('dismisses the created link on Done', async () => {
    const created: CreatedMemberInvite = { token: 'tok_abc123', invite: invite() };
    const { fixture, host } = await render({ list: () => of([]), create: () => of(created) });

    fixture.componentInstance.createRole = 'Manager';
    buttonWithText(host, 'Create invite').click();
    fixture.detectChanges();
    buttonWithText(host, 'Done').click();
    fixture.detectChanges();

    expect(host.textContent).not.toContain('will not be shown again');
  });

  it('cancels a pending invite after confirmation', async () => {
    const cancel = jest.fn(() => of(invite({ status: 'Cancelled' })));
    const { fixture, host } = await render({ list: () => of([invite()]), cancel });

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();
    buttonWithText(host, 'Cancel invite').click();
    fixture.detectChanges();

    expect(cancel).toHaveBeenCalledWith(1);
    expect(host.textContent).toContain('Cancelled');
  });

  it('keeps the invite when cancellation is dismissed, without calling the API', async () => {
    const cancel = jest.fn();
    const { fixture, host } = await render({ list: () => of([invite()]), cancel });

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();
    buttonWithText(host, 'Keep invite').click();
    fixture.detectChanges();

    expect(cancel).not.toHaveBeenCalled();
  });

  it('shows the API refusal verbatim when a cancellation is refused', async () => {
    const error = jest.fn();
    const message = 'This invite has already been used, so it cannot be cancelled.';
    const cancel = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host } = await render({ list: () => of([invite()]), cancel }, { error });

    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();
    buttonWithText(host, 'Cancel invite').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith(message);
  });
});
