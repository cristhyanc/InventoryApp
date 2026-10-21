import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';
import { AccessService } from '../access/access.service';
import { Capability } from '../access/capabilities';
import { NayaxConnectionCheckLine, NayaxConnectionService } from '../services/nayax-connection.service';
import { ProviderAvailabilityService } from './provider-availability.service';
import { SpecConnectionStatus, nayaxConnectionStatusDouble } from './provider-availability.spec-helpers';
import { PROVIDER_LOCKED_PATH } from './provider-lock';
import { ProviderLockedComponent } from './provider-locked.component';

@Component({ standalone: true, template: 'elsewhere' })
class BlankPageComponent {}

interface Rendered {
  fixture: ComponentFixture<ProviderLockedComponent>;
  host: HTMLElement;
}

/**
 * Renders the page the way `requireProvider` reaches it: at `/needs-provider?from=<refused URL>`,
 * over the shared availability signal of issue #728 reading one stubbed status.
 */
async function render(
  options: {
    from?: string;
    status?: SpecConnectionStatus;
    checks?: readonly NayaxConnectionCheckLine[];
    capability?: (capability: Capability) => Observable<boolean>;
  } = {}
): Promise<Rendered> {
  await TestBed.configureTestingModule({
    imports: [ProviderLockedComponent],
    providers: [
      provideRouter([
        { path: PROVIDER_LOCKED_PATH, component: BlankPageComponent },
        { path: '**', component: BlankPageComponent }
      ]),
      { provide: NayaxConnectionService, useValue: nayaxConnectionStatusDouble(options.status ?? 'NotConfigured') },
      {
        provide: AccessService,
        useValue: { allows: options.capability ?? ((): Observable<boolean> => of(true)) }
      }
    ]
  }).compileComponents();

  if (options.checks !== undefined) {
    TestBed.inject(ProviderAvailabilityService).accept(
      { status: 'PendingPermissions', machineChecksPending: false },
      options.checks
    );
  }

  const url =
    options.from === undefined
      ? `/${PROVIDER_LOCKED_PATH}`
      : `/${PROVIDER_LOCKED_PATH}?from=${encodeURIComponent(options.from)}`;
  await TestBed.inject(Router).navigateByUrl(url);

  const fixture = TestBed.createComponent(ProviderLockedComponent);
  fixture.detectChanges();

  return { fixture, host: fixture.nativeElement as HTMLElement };
}

function connectLink(host: HTMLElement): HTMLAnchorElement | null {
  return host.querySelector<HTMLAnchorElement>('a[href="/admin/nayax-settings"]');
}

/**
 * The "needs a provider" page (issue #729). It explains and never refuses: the endpoints behind the
 * page it stands in for authorize themselves, so what is asserted here is whether an operator is
 * told the right thing and offered the one action that fixes it.
 */
describe('ProviderLockedComponent with no usable connection (issue #729)', () => {
  it('names the destination that was refused and says Nayax is not connected', async () => {
    const { host } = await render({ from: '/sites', status: 'NotConfigured' });

    expect(host.querySelector('h1')?.textContent).toContain('Sites');
    expect(host.textContent).toContain('Sites is unavailable because Nayax is not connected.');
  });

  it('offers Connect to a member who may manage integrations', async () => {
    const { host } = await render({ from: '/sites', status: 'NotConfigured' });

    expect(connectLink(host)?.textContent).toContain('Connect Nayax');
  });

  /** A member who cannot reach the Integrations page is told who can, not offered a dead button. */
  it('offers no Connect button to a member who may not manage integrations', async () => {
    const { host } = await render({
      from: '/sites',
      status: 'NotConfigured',
      capability: () => of(false)
    });

    expect(connectLink(host)).toBeNull();
    expect(host.textContent).toContain('Ask the business Owner to connect Nayax');
  });

  /**
   * Decision P5 in one sentence on the page: nothing has been lost, and the reports over sales
   * already imported are still open.
   */
  it('says what still works without a provider, and is never a dead end', async () => {
    const { host } = await render({ from: '/sites', status: 'NotConfigured' });

    expect(host.textContent).toContain('Inventory, purchases and expenses work without a provider');
    expect(host.querySelector('a[href="/"]')?.textContent).toContain('Dashboard');
  });

  it('reports credentials that are stored and known not to work as a connection to fix', async () => {
    const { host } = await render({ from: '/sites', status: 'NeedsAttention' });

    expect(host.textContent).toContain('Nayax is not connected');
    expect(connectLink(host)).not.toBeNull();
  });

  it('names the site commission report, which is a live provider destination too', async () => {
    const { host } = await render({ from: '/reports/site-commissions', status: 'NotConfigured' });

    expect(host.querySelector('h1')?.textContent).toContain('Site Commissions');
  });

  /** A detail URL belongs to its list destination, so the page names that. */
  it('names the list destination a detail URL belongs to', async () => {
    const { host } = await render({ from: '/sites/3/products', status: 'NotConfigured' });

    expect(host.querySelector('h1')?.textContent).toContain('Sites');
  });

  /**
   * The machine list left the provider section in issue #740, so the menu declares no live read for
   * it and this page has nothing to say about one. The machine **details** route is still guarded,
   * and a request for it therefore gets the page's own generic explanation rather than a sentence
   * about a read the navigation no longer claims - naming the destination's live read again belongs
   * to the provider machine review (issue #742).
   */
  it('names no live read for the machine list, which needs no provider since issue #740', async () => {
    const { host } = await render({ from: '/machines/7', status: 'NotConfigured' });

    expect(host.querySelector('h1')?.textContent).toContain('needs Nayax');
    expect(host.textContent).toContain('no Nayax connection was named');
  });
});

describe('ProviderLockedComponent waiting for a Nayax permission (issue #729)', () => {
  const refused: readonly NayaxConnectionCheckLine[] = [
    { check: 'Machines', status: 'Failed', failure: 'PermissionNotGranted' }
  ];

  it('says the destination is waiting for a Nayax permission, not that Nayax is disconnected', async () => {
    const { host } = await render({ from: '/sites', status: 'PendingPermissions', checks: refused });

    expect(host.querySelector('h1')?.textContent).toContain('Sites is waiting for a Nayax permission');
    expect(host.textContent).toContain('The connection itself is working.');
    expect(host.textContent).not.toContain('Nayax is not connected');
  });

  /**
   * It names the refused **read** and its endpoint, which is what a support request to Nayax has to
   * ask for. The Nayax permission key behind that read has no published name, so none is printed.
   */
  it('names the refused Nayax read and the endpoint behind it', async () => {
    const { host } = await render({ from: '/sites', status: 'PendingPermissions', checks: refused });

    expect(host.textContent).toContain('Machine list (GET machines)');
  });

  /** Re-entering credentials that already work fixes nothing, so Connect is not offered. */
  it('offers no Connect button, because the credentials are not the problem', async () => {
    const { host } = await render({ from: '/sites', status: 'PendingPermissions', checks: refused });

    expect(connectLink(host)).toBeNull();
    expect(host.textContent).toContain('re-test the connection on the Integrations page');
  });
});

describe('ProviderLockedComponent reached with nothing to explain (issue #729)', () => {
  /**
   * The page re-reads the availability answer rather than trusting the URL that reached it, so a
   * connection made in another tab leaves it saying the page is available instead of insisting it
   * is locked.
   */
  it('says the page is available when the provider can serve it after all', async () => {
    const { host } = await render({ from: '/sites', status: 'Ready' });

    expect(host.textContent).toContain('Nayax can serve this page now');
    expect(host.querySelector('a[href="/sites"]')?.textContent).toContain('Open Sites');
    expect(connectLink(host)).toBeNull();
  });

  it('explains itself without naming a destination when it is opened directly', async () => {
    const { host } = await render({ status: 'NotConfigured' });

    expect(host.querySelector('h1')?.textContent).toContain('needs Nayax');
    expect(host.textContent).toContain('no Nayax connection was named');
  });

  /** A URL that is not a live provider destination names none either, rather than guessing one. */
  it('names no destination for a URL that needs no provider', async () => {
    const { host } = await render({ from: '/products', status: 'NotConfigured' });

    expect(host.textContent).toContain('no Nayax connection was named');
    expect(host.querySelector('h1')?.textContent).not.toContain('Products');
  });
});
