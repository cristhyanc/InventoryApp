import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AdminUsersComponent } from './admin-users.component';
import { MemberService } from '../../../services/member.service';
import { MemberInviteService } from '../../../services/member-invite.service';

/**
 * The Admin → Users page (issue #534) is composition only: it renders the Members and Invites
 * sections, each a self-contained feature component with its own service calls (docs/architecture.md
 * § Page composition boundary, issue #191).
 */
describe('AdminUsersComponent (issue #534)', () => {
  it('composes the Members and Invites sections', async () => {
    await TestBed.configureTestingModule({
      imports: [AdminUsersComponent],
      providers: [
        provideRouter([]),
        { provide: MemberService, useValue: { list: () => of([]) } },
        { provide: MemberInviteService, useValue: { list: () => of([]) } }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(AdminUsersComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('app-member-management')).toBeTruthy();
    expect(host.querySelector('app-member-invites')).toBeTruthy();
    expect(host.textContent).toContain('Users');
  });
});
