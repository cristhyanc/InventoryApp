import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { BrowserCacheLocation, Configuration, IPublicClientApplication, RedirectRequest } from '@azure/msal-browser';
import {
  PENDING_INVITE_TOKEN_KEY,
  storePendingInviteToken,
  takePendingInviteToken
} from '../access/pending-invite-token';
import { AuthorityId, apiScopeFor, clientIdFor, createMsalConfig } from '../auth-config';
import { MSAL_REDIRECT_GUARD, SignInAuthorityGuard } from './browser-auth-providers';
import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import { INTENDED_DESTINATION_KEY, peekIntendedDestination } from './intended-destination';
import {
  forgetSelectedAuthority,
  needsAuthorityChoice,
  readSelectedAuthority,
  rememberSelectedAuthority
} from './selected-authority';
import { AUTHORITY_CLIENT_FACTORY, SignInService } from './sign-in.service';

/**
 * The browser half of issue #714's verification matrix: the customer identity journeys **composed**,
 * from the guard that refuses a page to the MSAL request the redirect is actually made with.
 *
 * The unit specs beside this one each pin one piece - `selected-authority.spec.ts` the per-tab
 * snapshot, `intended-destination.spec.ts` the refusals, `sign-in.service.spec.ts` the request,
 * `pending-invite-token.spec.ts` the store. What none of them can say is whether those pieces still
 * hold together as one journey, which is what the issue asks about: an interrupted invite
 * acceptance, a cancelled hosted sign-in and the retry after it, a provider switch inside one
 * browser, and the § 15.1 rollback lever landing on a tab part-way through. So the pieces here are
 * the real ones, and the only double is the `PublicClientApplication` itself - jsdom has no
 * `crypto.subtle` - which is built **from `createMsalConfig(authority)`** so the configuration under
 * assertion is the one a real journey would use.
 *
 * **What this cannot prove.** No provider is contacted and no token is issued, so nothing here is
 * evidence that a hosted Google or email-and-password journey exists, is offered in the tenant's user
 * flow, or can be completed. That is a human staging check; see
 * `docs/identity-rollout-verification.md` for the matrix that keeps the two apart.
 */
describe('the customer identity journeys end to end (issue #714)', () => {
  interface StartedRedirect {
    authority: AuthorityId;
    configuration: Configuration;
    request: RedirectRequest;
  }

  interface Harness {
    service: SignInService;
    started: StartedRedirect[];
  }

  function createHarness(): Harness {
    const started: StartedRedirect[] = [];

    // The authority's own MSAL client, built from the application's own configuration for it.
    const clientFor = (authority: AuthorityId): IPublicClientApplication => {
      const configuration = createMsalConfig(authority);

      return {
        getConfiguration: () => configuration,
        loginRedirect: (request: RedirectRequest) => {
          started.push({ authority, configuration, request });
          return Promise.resolve();
        }
      } as unknown as IPublicClientApplication;
    };

    TestBed.configureTestingModule({
      providers: [
        SignInAuthorityGuard,
        { provide: MSAL_REDIRECT_GUARD, useValue: { canActivate: () => true } },
        // Nobody is signed in: this is a signed-out visitor's journey.
        {
          provide: MsalService,
          useValue: {
            instance: { ...clientFor('workforce'), getAllAccounts: () => [] }
          }
        },
        {
          provide: AUTHORITY_CLIENT_FACTORY,
          useValue: (authority: AuthorityId) => Promise.resolve(clientFor(authority))
        },
        provideRouter([])
      ]
    });

    return { service: TestBed.inject(SignInService), started };
  }

  /** What the guard in front of `MsalGuard` decides for a signed-out visitor asking for `url`. */
  function visit(url: string): unknown {
    return TestBed.inject(SignInAuthorityGuard).canActivate(
      {} as ActivatedRouteSnapshot,
      { url } as RouterStateSnapshot
    );
  }

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    configureExternalId();
  });

  afterEach(() => {
    unconfigureExternalId();
    TestBed.resetTestingModule();
  });

  /**
   * The journey the issue calls an interrupted invite journey, in one piece: an invite link opened
   * while signed out, a hosted sign-in cancelled on the provider's page, and a second attempt that
   * still has to land on the invitation. The invite token is a bearer credential, so the assertion
   * that matters is where it is *not*: not in the MSAL request, not in the authority's cache
   * location, not in the destination held across the redirect - and still readable by the
   * acceptance page at the end.
   */
  it('keeps an interrupted invite journey intact without the token reaching an authority', async () => {
    const { service, started } = createHarness();
    const token = 'an-invite-token-nobody-else-may-see';

    // The address-bar guard has already taken the token out of the URL by the time any
    // `canActivate` guard runs, so what the visitor is refused is the tokenless `/invite`.
    storePendingInviteToken(token);
    const decision = visit('/invite');

    expect(decision).toBeInstanceOf(UrlTree);
    expect(String(decision)).toBe('/sign-in');
    expect(peekIntendedDestination()).toBe('/invite');

    await service.signIn('externalId');

    const [first] = started;
    expect(first.authority).toBe('externalId');
    expect(first.request.redirectStartPage).toBe('/invite');
    expect(JSON.stringify(first.request)).not.toContain(token);
    expect(JSON.stringify(first.configuration)).not.toContain(token);

    // MSAL's cache lives in `localStorage`, which is shared by every tab and outlives this one.
    expect(JSON.stringify(localStorage)).not.toContain(token);
    expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).not.toContain(token);

    // Cancelled on the hosted page: reported, and nothing retried by itself.
    service.report({ errorCode: 'access_denied' });
    expect(service.take()).toBe('cancelled');
    expect(started).toHaveLength(1);

    // The second attempt still goes back to the invitation.
    await service.signIn('externalId');
    expect(started[1].request.redirectStartPage).toBe('/invite');

    // And the acceptance page can still read the invite it was opened with.
    expect(takePendingInviteToken()).toBe(token);
    expect(sessionStorage.getItem(PENDING_INVITE_TOKEN_KEY)).toBeNull();
  });

  /**
   * Cache separation, asserted on the two configurations a journey through each authority actually
   * builds (design § 9.2). MSAL partitions its cache by client id and authority, so those two values
   * differing is the separation; both journeys keeping the same `localStorage` cache location is what
   * makes the partition the only thing keeping them apart, which is why it is asserted rather than
   * assumed.
   */
  it('never shares a client id, an authority or an API audience between the two journeys', async () => {
    const { service, started } = createHarness();

    await service.signIn('externalId');
    forgetSelectedAuthority();
    await service.signIn('workforce');

    const [customerIdentity, workforce] = started;

    expect(customerIdentity.configuration.auth.clientId).toBe(clientIdFor('externalId'));
    expect(workforce.configuration.auth.clientId).toBe(clientIdFor('workforce'));
    expect(customerIdentity.configuration.auth.clientId).not.toBe(workforce.configuration.auth.clientId);
    expect(customerIdentity.configuration.auth.authority).not.toBe(workforce.configuration.auth.authority);

    expect(customerIdentity.configuration.cache?.cacheLocation).toBe(BrowserCacheLocation.LocalStorage);
    expect(workforce.configuration.cache?.cacheLocation).toBe(BrowserCacheLocation.LocalStorage);

    // Each journey asks for its own tenant's API audience and never the other's: the API's two
    // bearer schemes validate only their own, so asking for the wrong one acquires a refused token.
    expect(customerIdentity.request.scopes).toContain(apiScopeFor('externalId'));
    expect(customerIdentity.request.scopes).not.toContain(apiScopeFor('workforce'));
    expect(workforce.request.scopes).toEqual([apiScopeFor('workforce')]);
  });

  /**
   * Switching provider: a sign-out forgets the choice, so the next person at this browser is asked
   * again and the journey they choose is the only one asked for a token. There is no silent fallback
   * and no leftover authority.
   */
  it('asks only the newly chosen authority for a token after a provider switch', async () => {
    const { service, started } = createHarness();

    await service.signIn('externalId');
    expect(readSelectedAuthority()).toBe('externalId');

    // Signing out ends that authority's session and forgets the choice (design § 9.3).
    forgetSelectedAuthority();
    expect(needsAuthorityChoice(false)).toBe(true);

    await service.signIn('workforce');

    expect(readSelectedAuthority()).toBe('workforce');
    expect(started.map((redirect) => redirect.authority)).toEqual(['externalId', 'workforce']);
  });

  /**
   * The § 15.1 rollback lever reaching a tab that is part-way through the new journey: the
   * identifiers are cleared, and this browser goes back to workforce sign-in without being asked
   * anything. The remembered choice does not resurrect an authority the deployment no longer has,
   * the journey against it cannot even be started, and the invitation still waiting is untouched -
   * an invite is a token and a role, not an identity (design § 15.1's invite row).
   */
  it('takes a tab part-way through the new journey back to workforce sign-in on a rollback', async () => {
    const { service, started } = createHarness();
    storePendingInviteToken('an-invite-that-outlives-the-rollback');
    rememberSelectedAuthority('externalId');
    expect(readSelectedAuthority()).toBe('externalId');

    // A human clears the four identifiers.
    unconfigureExternalId();

    expect(readSelectedAuthority()).toBe('workforce');
    expect(needsAuthorityChoice(false)).toBe(false);
    expect(visit('/reports/daily')).toBe(true);

    await expect(service.signIn('externalId')).rejects.toBeDefined();
    expect(started).toHaveLength(0);

    await service.signIn('workforce');
    expect(started[0].configuration.auth.clientId).toBe(clientIdFor('workforce'));
    expect(readSelectedAuthority()).toBe('workforce');

    expect(takePendingInviteToken()).toBe('an-invite-that-outlives-the-rollback');
  });

  /**
   * The open-redirect protection is "there is no caller-supplied return URL" (design § 9.1), and
   * the store the destination waits in is readable by anything running in this tab. A destination
   * that is not a page of this application therefore never becomes a redirect start page, however it
   * got there.
   */
  it('never starts a redirect back to somewhere outside this application', async () => {
    const { service, started } = createHarness();

    for (const url of ['https://not-this-app.example/steal', '//not-this-app.example', '/auth', '/sign-in']) {
      sessionStorage.setItem(INTENDED_DESTINATION_KEY, JSON.stringify({ url, storedAt: Date.now() }));

      await service.signIn('externalId');

      expect(started[started.length - 1].request.redirectStartPage).toBeUndefined();
      expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
    }
  });
});
