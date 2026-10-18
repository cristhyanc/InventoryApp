import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { NayaxConnectionService, NayaxConnectionState, NayaxConnectionStatusName } from '../../../services/nayax-connection.service';
import { NayaxStatusBannerComponent } from './nayax-status-banner.component';

function state(status: NayaxConnectionStatusName): NayaxConnectionState {
  return { status, machineChecksPending: false };
}

interface ConnectionStub {
  status: jest.Mock<Observable<NayaxConnectionState>, []>;
}

async function render(statusSource: Observable<NayaxConnectionState>, canManage: boolean) {
  const connections: ConnectionStub = { status: jest.fn(() => statusSource) };

  await TestBed.configureTestingModule({
    imports: [NayaxStatusBannerComponent],
    providers: [
      provideRouter([]),
      { provide: NayaxConnectionService, useValue: connections },
      { provide: AccessService, useValue: { allows: () => of(canManage) } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxStatusBannerComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;

  return { fixture, host, component: fixture.componentInstance, connections };
}

const banner = (host: HTMLElement) => host.querySelector('[data-testid="nayax-status-banner"]');

/**
 * The shared Nayax connection status banner (issue #539), composed into every screen that depends
 * on a live Nayax read. Its whole specification is: hidden while the connection is `Ready`, while
 * the status is still unread, or when it could not be read at all, and otherwise shown with a link
 * for an Owner alone.
 */
describe('NayaxStatusBannerComponent (issue #539)', () => {
  it.each<NayaxConnectionStatusName>(['NotConfigured', 'PendingPermissions', 'NeedsAttention'])(
    'shows the banner for an Owner when the status is %s',
    async (status) => {
      const { host } = await render(of(state(status)), true);

      expect(banner(host)).not.toBeNull();
      expect(banner(host)?.textContent).toContain("Nayax isn't fully connected for this business.");
    }
  );

  it.each<NayaxConnectionStatusName>(['NotConfigured', 'PendingPermissions', 'NeedsAttention'])(
    'shows the banner for a non-Owner when the status is %s',
    async (status) => {
      const { host } = await render(of(state(status)), false);

      expect(banner(host)).not.toBeNull();
    }
  );

  it('hides the banner once the status is Ready, for an Owner', async () => {
    const { host } = await render(of(state('Ready')), true);

    expect(banner(host)).toBeNull();
  });

  it('hides the banner once the status is Ready, for a non-Owner', async () => {
    const { host } = await render(of(state('Ready')), false);

    expect(banner(host)).toBeNull();
  });

  it('hides the banner while the status has not been read yet', async () => {
    const neverAnswers = new Subject<NayaxConnectionState>();
    const { host } = await render(neverAnswers.asObservable(), true);

    expect(banner(host)).toBeNull();
  });

  it('reads the status once for several banners on one screen (issue #728)', async () => {
    const connections: ConnectionStub = { status: jest.fn(() => of(state('NeedsAttention'))) };

    await TestBed.configureTestingModule({
      imports: [NayaxStatusBannerComponent],
      providers: [
        provideRouter([]),
        { provide: NayaxConnectionService, useValue: connections },
        { provide: AccessService, useValue: { allows: () => of(true) } }
      ]
    }).compileComponents();

    for (const _ of [0, 1, 2]) {
      TestBed.createComponent(NayaxStatusBannerComponent).detectChanges();
    }

    expect(connections.status).toHaveBeenCalledTimes(1);
  });

  it('hides the banner when the status read fails, rather than guessing it is not Ready', async () => {
    const { host, connections } = await render(throwError(() => new Error('boom')), true);

    expect(connections.status).toHaveBeenCalledTimes(1);
    expect(banner(host)).toBeNull();
  });

  it('gives an Owner a link to the connection wizard, and no plain-text instruction', async () => {
    const { host } = await render(of(state('NeedsAttention')), true);

    const link = host.querySelector<HTMLAnchorElement>('[data-testid="nayax-status-banner-link"]');
    expect(link?.getAttribute('href')).toBe('/admin/nayax-settings');
    expect(host.querySelector('[data-testid="nayax-status-banner-no-link"]')).toBeNull();
  });

  it('tells a Manager or Operator to ask an Owner, with no link at all', async () => {
    const { host } = await render(of(state('NeedsAttention')), false);

    expect(host.querySelector('[data-testid="nayax-status-banner-link"]')).toBeNull();
    const notice = host.querySelector('[data-testid="nayax-status-banner-no-link"]');
    expect(notice?.textContent?.trim()).toBe(
      "Nayax isn't fully connected for this business. Ask a business Owner to finish the Nayax connection."
    );
  });
});
