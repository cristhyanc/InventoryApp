import { TestBed } from '@angular/core/testing';
import { MsalService } from '@azure/msal-angular';
import { IPublicClientApplication } from '@azure/msal-browser';
import { apiScopeFor, clientIdFor } from '../auth-config';
import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import {
  INTENDED_DESTINATION_KEY,
  INTENDED_DESTINATION_LIFETIME_MS,
  rememberIntendedDestination
} from './intended-destination';
import { SELECTED_AUTHORITY_KEY } from './selected-authority';
import { AUTHORITY_CLIENT_FACTORY, SignInService } from './sign-in.service';

interface Harness {
  service: SignInService;
  bootstrappedLoginRedirect: jest.Mock;
  customerIdentityLoginRedirect: jest.Mock;
  createClient: jest.Mock;
}

function client(clientId: string, loginRedirect: jest.Mock): IPublicClientApplication {
  return {
    getConfiguration: () => ({ auth: { clientId } }),
    loginRedirect
  } as unknown as IPublicClientApplication;
}

function createHarness(): Harness {
  const bootstrappedLoginRedirect = jest.fn().mockResolvedValue(undefined);
  const customerIdentityLoginRedirect = jest.fn().mockResolvedValue(undefined);
  const createClient = jest.fn(async () =>
    client(clientIdFor('externalId'), customerIdentityLoginRedirect)
  );

  TestBed.configureTestingModule({
    providers: [
      {
        provide: MsalService,
        useValue: { instance: client(clientIdFor('workforce'), bootstrappedLoginRedirect) }
      },
      { provide: AUTHORITY_CLIENT_FACTORY, useValue: createClient }
    ]
  });

  return {
    service: TestBed.inject(SignInService),
    bootstrappedLoginRedirect,
    customerIdentityLoginRedirect,
    createClient
  };
}

/**
 * Starting a sign-in against a chosen authority (issue #711).
 */
describe('SignInService (issue #711)', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    configureExternalId();
  });

  afterEach(() => {
    unconfigureExternalId();
    TestBed.resetTestingModule();
  });

  it('records the chosen authority before the redirect, so the page it returns to bootstraps it', async () => {
    const { service } = createHarness();

    await service.signIn('externalId');

    expect(localStorage.getItem(SELECTED_AUTHORITY_KEY)).toBe('externalId');
  });

  /**
   * MSAL partitions its cache by client id and authority. Choosing the authority the bootstrapped
   * instance was *not* built for must build that authority's own instance rather than repoint the
   * running one, which is the shape that serves one authority's token out of the other's cache
   * (design § 9.2).
   */
  it('redirects with the chosen authority own instance, never the bootstrapped one', async () => {
    const { service, bootstrappedLoginRedirect, customerIdentityLoginRedirect, createClient } = createHarness();

    await service.signIn('externalId');

    expect(createClient).toHaveBeenCalledWith('externalId');
    expect(customerIdentityLoginRedirect).toHaveBeenCalledTimes(1);
    expect(bootstrappedLoginRedirect).not.toHaveBeenCalled();
  });

  it('reuses the bootstrapped instance when it already belongs to the chosen authority', async () => {
    const { service, bootstrappedLoginRedirect, createClient } = createHarness();

    await service.signIn('workforce');

    expect(createClient).not.toHaveBeenCalled();
    expect(bootstrappedLoginRedirect).toHaveBeenCalledTimes(1);
  });

  it('asks the hosted flow for the profile scope and its own API audience, and no provider hint', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();

    await service.signIn('externalId');

    const request = customerIdentityLoginRedirect.mock.calls[0][0];
    expect(request.scopes).toContain('profile');
    expect(request.scopes).toContain(apiScopeFor('externalId'));

    // The provider is chosen on the tenant's own hosted sign-in page (design § 13 item 5). This
    // application invents no `domain_hint`, `login_hint` or other provider-selection parameter.
    expect(request.extraQueryParameters).toBeUndefined();
    expect(request.domainHint).toBeUndefined();
  });

  /**
   * Issue #712. Every credential step of the local journey - the password, the mailbox
   * verification code and the reset code - happens on the provider's hosted pages, so the request
   * this application starts carries scopes and, at most, the page to come back to. Nothing may
   * creep into it that could carry a credential or a mailbox: no `loginHint`, no `prompt`, no
   * extra query parameter, and no key this test does not name.
   */
  it('starts the hosted journey with no credential, mailbox or recovery parameter of its own', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();
    rememberIntendedDestination('/reports/daily');

    await service.signIn('externalId');

    const request = customerIdentityLoginRedirect.mock.calls[0][0] as Record<string, unknown>;
    expect(Object.keys(request).sort()).toEqual(['redirectStartPage', 'scopes']);
    expect(request['loginHint']).toBeUndefined();
    expect(request['prompt']).toBeUndefined();
    expect(JSON.stringify(request).toLowerCase()).not.toContain('password');
  });

  it('hands MSAL the page the visitor asked for, and keeps it until the sign-in completes', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();
    rememberIntendedDestination('/reports/daily');

    await service.signIn('externalId');

    expect(customerIdentityLoginRedirect.mock.calls[0][0].redirectStartPage).toBe('/reports/daily');
    // Still held: only the callback that completes releases it (`AppComponent`), because a
    // cancelled or refused journey comes back carrying nothing and has to be retried with it.
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).not.toBeNull();
  });

  it('starts a redirect with no start page when nothing was asked for', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();

    await service.signIn('externalId');

    expect(customerIdentityLoginRedirect.mock.calls[0][0].redirectStartPage).toBeUndefined();
  });

  /**
   * "No silent fallback, ever" (design § 9.2). A chosen authority that cannot start a redirect is
   * an error to report, never a reason to try the other one: that would silently change who the
   * caller is, and with `(tid, oid)` it would silently change which business they resolve.
   */
  it('reports a redirect that could not start and never tries the other authority', async () => {
    const { service, bootstrappedLoginRedirect, customerIdentityLoginRedirect } = createHarness();
    customerIdentityLoginRedirect.mockRejectedValue({ errorCode: 'network_error' });

    await expect(service.signIn('externalId')).rejects.toBeDefined();

    expect(bootstrappedLoginRedirect).not.toHaveBeenCalled();
  });

  /**
   * A redirect that never left the page is exactly the case the sign-in page offers a retry for,
   * and that retry has to go back to the page the visitor asked for - not quietly to the home
   * page. The destination is therefore still held after the failure.
   */
  it('keeps the destination when the redirect could not start, so the retry still returns there', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();
    rememberIntendedDestination('/reports/daily');
    customerIdentityLoginRedirect.mockRejectedValueOnce({ errorCode: 'network_error' });

    await expect(service.signIn('externalId')).rejects.toBeDefined();
    await service.signIn('externalId');

    expect(customerIdentityLoginRedirect.mock.calls[1][0].redirectStartPage).toBe('/reports/daily');
  });

  /** The same, for a failure before the authority's own instance even exists, and for the one
   * destination that matters most: an invite the person was part-way through accepting. */
  it('keeps an invite destination when the chosen authority client could not be built', async () => {
    const { service, customerIdentityLoginRedirect, createClient } = createHarness();
    rememberIntendedDestination('/invite');
    createClient.mockRejectedValueOnce(new Error('crypto unavailable'));

    await expect(service.signIn('externalId')).rejects.toBeDefined();
    await service.signIn('externalId');

    expect(customerIdentityLoginRedirect.mock.calls[0][0].redirectStartPage).toBe('/invite');
  });

  /** Retrying reuses the destination rather than re-recording it, so an abandoned sign-in still
   * expires exactly when it would have. */
  it('does not extend the destination window by retrying inside it', async () => {
    const { service, customerIdentityLoginRedirect } = createHarness();
    const storedAt = Date.now() - INTENDED_DESTINATION_LIFETIME_MS + 1_000;
    sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url: '/reports/daily', storedAt }));
    customerIdentityLoginRedirect.mockRejectedValueOnce({ errorCode: 'network_error' });

    await expect(service.signIn('externalId')).rejects.toBeDefined();
    await service.signIn('externalId');

    const held = JSON.parse(sessionStorage.getItem(INTENDED_DESTINATION_KEY) ?? 'null') as { storedAt: number };
    expect(held.storedAt).toBe(storedAt);
    expect(customerIdentityLoginRedirect.mock.calls[1][0].redirectStartPage).toBe('/reports/daily');
  });

  it('tells a cancelled hosted sign-in apart from a broken one', () => {
    const { service } = createHarness();

    for (const cancelled of ['access_denied', 'user_cancelled', 'AADB2C90091']) {
      service.report({ errorCode: cancelled });
      expect(service.take()).toBe('cancelled');
    }

    for (const broken of [{ errorCode: 'invalid_grant' }, new Error('boom'), null, undefined]) {
      service.report(broken);
      expect(service.take()).toBe('failed');
    }
  });

  it('reports a failure once, so a later visit to the sign-in page is not haunted by it', () => {
    const { service } = createHarness();

    service.report({ errorCode: 'access_denied' });

    expect(service.take()).toBe('cancelled');
    expect(service.take()).toBeNull();
  });

  it('clears a previous failure when a new sign-in starts', async () => {
    const { service } = createHarness();
    service.report({ errorCode: 'access_denied' });

    await service.signIn('workforce');

    expect(service.take()).toBeNull();
  });
});
