import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../app.routes';
import { InviteLinkReopenComponent } from './invite-link-reopen.component';

/**
 * The tokenless `/invite` page (issue #681, finding 4 of the #328 implementation review): the
 * address the invite page leaves in the address bar, reached again by a reload or by Back. It must
 * explain that the link has to be opened again rather than fall through to the wildcard redirect.
 */
describe('InviteLinkReopenComponent (issue #681)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('is what the real route table serves at /invite, instead of the wildcard redirect', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes)] });
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/invite', InviteLinkReopenComponent);

    expect(TestBed.inject(Router).url).toBe('/invite');
    expect(harness.routeNativeElement?.textContent).toContain('Open your invite link again');
  });

  it('tells the person to open the invite link again, and offers a way on', async () => {
    TestBed.configureTestingModule({ imports: [InviteLinkReopenComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(InviteLinkReopenComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('Open the invite link you were sent again');
    expect(host.querySelector('a')?.getAttribute('href')).toBe('/');
  });
});
