import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { MemberManagementComponent } from './member-management.component';
import { BusinessMember, MemberService } from '../../../services/member.service';
import { ToastService } from '../../../services/toast.service';

function member(overrides: Partial<BusinessMember> = {}): BusinessMember {
  return {
    membershipId: 1,
    displayName: 'Dana Lee',
    email: 'dana@example.com',
    role: 'Manager',
    isActive: true,
    joinedAtUtc: '2026-01-01T00:00:00Z',
    ...overrides
  };
}

async function render(memberService: Partial<MemberService>, toast?: Partial<ToastService>) {
  await TestBed.configureTestingModule({
    imports: [MemberManagementComponent],
    providers: [
      { provide: MemberService, useValue: memberService },
      { provide: ToastService, useValue: { success: jest.fn(), error: jest.fn(), warning: jest.fn(), info: jest.fn(), ...toast } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(MemberManagementComponent);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

/**
 * The Members section of the Admin → Users page (issue #534), over issue #533's four endpoints.
 */
describe('MemberManagementComponent (issue #534)', () => {
  it('loads and displays every member of the business', async () => {
    const list = jest.fn(() => of([member()]));
    const { host } = await render({ list });

    expect(list).toHaveBeenCalledTimes(1);
    expect(host.textContent).toContain('Dana Lee');
    expect(host.textContent).toContain('dana@example.com');
    expect(host.textContent).toContain('Active');
  });

  it('shows an error state when the list fails to load', async () => {
    const { host } = await render({ list: () => throwError(() => new Error('boom')) });

    expect(host.textContent).toContain('Unable to load members.');
  });

  it('shows an empty state when there are no members', async () => {
    const { host } = await render({ list: () => of([]) });

    expect(host.textContent).toContain('No members found.');
  });

  it('asks for confirmation before changing a role, and calls nothing until confirmed', async () => {
    const changeRole = jest.fn(() => of(member({ role: 'Owner' })));
    const { fixture, host } = await render({ list: () => of([member()]), changeRole });

    const select = host.querySelector('select') as HTMLSelectElement;
    select.value = 'Owner';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(changeRole).not.toHaveBeenCalled();
    expect(host.textContent).toContain("Change Dana Lee's role to Owner?");

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(changeRole).toHaveBeenCalledWith(1, 'Owner');
  });

  it('does not prompt when the selected role is unchanged', async () => {
    const changeRole = jest.fn();
    const { fixture, host } = await render({ list: () => of([member()]), changeRole });

    const select = host.querySelector('select') as HTMLSelectElement;
    select.value = 'Manager';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(changeRole).not.toHaveBeenCalled();
    expect(host.textContent).not.toContain('Confirm');
  });

  it('shows the API\'s last-Owner refusal verbatim when a role change is refused', async () => {
    const error = jest.fn();
    const message = 'A business must keep at least one active Owner. Give another member the Owner role first.';
    const changeRole = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host } = await render({ list: () => of([member()]), changeRole }, { error });

    const select = host.querySelector('select') as HTMLSelectElement;
    select.value = 'Operator';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledWith(message);
  });

  it('revokes a member after confirmation and flips their status to show Reinstate instead', async () => {
    const revoke = jest.fn(() => of(member({ isActive: false })));
    const { fixture, host } = await render({ list: () => of([member()]), revoke });

    buttonWithText(host, 'Revoke').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(revoke).toHaveBeenCalledWith(1);
    expect(host.textContent).toContain('Revoked');
    expect(host.querySelector('button')).toBeTruthy();
    expect(buttonWithText(host, 'Reinstate')).toBeTruthy();
  });

  it('cancels a pending action without calling the API', async () => {
    const revoke = jest.fn();
    const { fixture, host } = await render({ list: () => of([member()]), revoke });

    buttonWithText(host, 'Revoke').click();
    fixture.detectChanges();
    buttonWithText(host, 'Cancel').click();
    fixture.detectChanges();

    expect(revoke).not.toHaveBeenCalled();
    expect(host.textContent).not.toContain('Revoke member');
  });

  it('shows the API\'s already-a-member refusal verbatim when a reinstatement is refused', async () => {
    const error = jest.fn();
    const message = 'This person is already a member of a business.';
    const reinstate = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host } = await render({ list: () => of([member({ isActive: false })]), reinstate }, { error });

    buttonWithText(host, 'Reinstate').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(reinstate).toHaveBeenCalledWith(1);
    expect(error).toHaveBeenCalledWith(message);
  });
});
