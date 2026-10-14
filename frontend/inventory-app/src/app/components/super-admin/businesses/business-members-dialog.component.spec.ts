import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { BusinessMembersDialogComponent } from './business-members-dialog.component';
import { SuperAdminBusinessMember, SuperAdminBusinessesService } from '../../../services/super-admin-businesses.service';

function businessMember(overrides: Partial<SuperAdminBusinessMember> = {}): SuperAdminBusinessMember {
  return { membershipId: 3, displayName: 'Dana Lee', role: 'Manager', isActive: true, ...overrides };
}

async function render(service: Partial<SuperAdminBusinessesService>) {
  await TestBed.configureTestingModule({
    imports: [BusinessMembersDialogComponent],
    providers: [{ provide: SuperAdminBusinessesService, useValue: service }]
  }).compileComponents();

  const fixture = TestBed.createComponent(BusinessMembersDialogComponent);
  fixture.componentRef.setInput('businessId', 9);
  fixture.componentRef.setInput('businessName', 'Snack Co');
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function buttonWithText(host: HTMLElement, text: string): HTMLButtonElement {
  const match = Array.from(host.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  expect(match).toBeDefined();
  return match!;
}

describe('BusinessMembersDialogComponent (issue #547)', () => {
  it('loads and displays the deactivated business\'s members', async () => {
    const members = jest.fn(() => of([businessMember()]));
    const { host } = await render({ members });

    expect(members).toHaveBeenCalledWith(9);
    expect(host.textContent).toContain('Dana Lee');
    expect(host.textContent).toContain('Manager');
    expect(host.textContent).toContain('Active');
  });

  it('shows an error state when the list fails to load', async () => {
    const { host } = await render({ members: () => throwError(() => new Error('boom')) });

    expect(host.textContent).toContain("Unable to load this business's members.");
  });

  it('shows an empty state when there are no members', async () => {
    const { host } = await render({ members: () => of([]) });

    expect(host.textContent).toContain('No members found.');
  });

  it('shows no release action for an already-released member', async () => {
    const { host } = await render({ members: () => of([businessMember({ isActive: false })]) });

    expect(host.textContent).toContain('Released');
    expect(Array.from(host.querySelectorAll('button')).some((button) => button.textContent?.trim() === 'Release')).toBe(
      false
    );
  });

  it('asks for confirmation before releasing, and calls nothing until confirmed', async () => {
    const releaseMember = jest.fn(() => of(businessMember({ isActive: false })));
    const { fixture, host } = await render({ members: () => of([businessMember()]), releaseMember });

    buttonWithText(host, 'Release').click();
    fixture.detectChanges();

    expect(releaseMember).not.toHaveBeenCalled();
    expect(host.textContent).toContain('Release Dana Lee from Snack Co?');

    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(releaseMember).toHaveBeenCalledWith(9, 3);
  });

  it('emits memberReleased and updates the row once the release succeeds', async () => {
    const releaseMember = jest.fn(() => of(businessMember({ isActive: false })));
    const notifyChanged = jest.fn();
    const { fixture, host, component } = await render({
      members: () => of([businessMember()]),
      releaseMember,
      notifyChanged
    });
    const memberReleased = jest.fn();
    component.memberReleased.subscribe(memberReleased);

    buttonWithText(host, 'Release').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(host.textContent).toContain('Released');
    expect(notifyChanged).toHaveBeenCalledTimes(1);
    expect(memberReleased).toHaveBeenCalledTimes(1);
  });

  it('shows the API refusal verbatim when a release is refused', async () => {
    const message = 'The business is not deactivated.';
    const releaseMember = jest.fn(() => throwError(() => ({ error: { message } })));
    const { fixture, host } = await render({ members: () => of([businessMember()]), releaseMember });

    buttonWithText(host, 'Release').click();
    fixture.detectChanges();
    buttonWithText(host, 'Confirm').click();
    fixture.detectChanges();

    expect(host.textContent).toContain(message);
  });

  it('emits closed from its own close control', async () => {
    const { fixture, host, component } = await render({ members: () => of([]) });
    const closed = jest.fn();
    component.closed.subscribe(closed);

    buttonWithText(host, 'Close').click();
    fixture.detectChanges();

    expect(closed).toHaveBeenCalledTimes(1);
  });
});
