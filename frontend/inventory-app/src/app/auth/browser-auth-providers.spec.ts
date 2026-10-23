import { readFileSync } from 'fs';
import { join } from 'path';
import { HTTP_INTERCEPTORS } from '@angular/common/http';
import { ClassProvider, FactoryProvider, Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { MsalBroadcastService, MsalGuard, MsalInterceptor, MsalService, MSAL_INSTANCE } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { initializeMsal } from '../app.config';
import { apiScopeFor } from '../auth-config';
import { ConfigService } from '../services/config.service';
import {
  MSALGuardConfigFactory,
  MSALInterceptorConfigFactory,
  MSAL_REDIRECT_GUARD,
  SignInAuthorityGuard,
  browserAuthProviders
} from './browser-auth-providers';
import { configureExternalId, unconfigureExternalId } from './external-id.spec-helpers';
import { INTENDED_DESTINATION_KEY, peekIntendedDestination } from './intended-destination';
import { SELECTED_AUTHORITY_KEY, readSelectedAuthority, rememberSelectedAuthority } from './selected-authority';
import {
  E2ETestAuthGuard,
  browserAuthProviders as e2eBrowserAuthProviders,
  createE2ETestMsalService
} from './browser-auth-providers.e2e';

/**
 * The frontend half of issue #46's fail-closed requirement.
 *
 * The browser-level E2E suite needs the application to render without an interactive Entra
 * sign-in, and the way that is done must not be reachable from a normal build. Two things
 * therefore have to stay true, and neither is visible by reading `app.config.ts`:
 *
 *  1. the module every build compiles still wires the real MSAL guard and interceptor; and
 *  2. the test replacement is declared in exactly one Angular build configuration, so a
 *     production or development bundle cannot contain it at all.
 *
 * The second assertion reads `angular.json` on purpose: the structural guarantee lives in the
 * build configuration, so that file is the thing under test.
 */
describe('browser auth providers', () => {
  const angularJson = JSON.parse(
    readFileSync(join(__dirname, '..', '..', '..', 'angular.json'), 'utf8')
  ) as {
    projects: Record<
      string,
      {
        architect: {
          build: { configurations: Record<string, { fileReplacements?: { replace: string; with: string }[] }> };
          serve: { configurations: Record<string, { buildTarget: string }> };
        };
      }
    >;
  };

  const buildConfigurations = angularJson.projects['inventory-app'].architect.build.configurations;
  const e2eModule = 'src/app/auth/browser-auth-providers.e2e.ts';

  function provideTokens(providers: Provider[]): unknown[] {
    return providers.map((provider) =>
      typeof provider === 'object' && provider !== null && 'provide' in provider ? provider.provide : provider
    );
  }

  function classProvider(token: unknown): ClassProvider | undefined {
    return browserAuthProviders.find(
      (provider): provider is ClassProvider =>
        typeof provider === 'object' && 'provide' in provider && provider.provide === token
    );
  }

  it('wires the real MSAL guard, interceptor and instance in the module every build compiles', () => {
    const tokens = provideTokens(browserAuthProviders);

    expect(tokens).toContain(MsalGuard);
    expect(tokens).toContain(MSAL_REDIRECT_GUARD);
    expect(tokens).toContain(MsalService);
    expect(tokens).toContain(MsalBroadcastService);
    expect(tokens).toContain(MSAL_INSTANCE);

    const interceptor = classProvider(HTTP_INTERCEPTORS);
    expect(interceptor?.useClass).toBe(MsalInterceptor);
    expect(interceptor?.multi).toBe(true);
  });

  /**
   * The authority choice of issue #711 reaches every protected route by taking the `MsalGuard`
   * token's place, because that is the token `app.routes.ts` names. The real guard is still wired,
   * under its own token, and is what the gate delegates to.
   */
  it('puts the authority choice in front of the real guard, keeping both', () => {
    expect(classProvider(MsalGuard)?.useClass).toBe(SignInAuthorityGuard);
    expect(classProvider(MSAL_REDIRECT_GUARD)?.useClass).toBe(MsalGuard);
  });

  it('declares the end-to-end replacement in exactly one build configuration', () => {
    const configurationsReplacingAuth = Object.entries(buildConfigurations)
      .filter(([, configuration]) =>
        (configuration.fileReplacements ?? []).some((replacement) => replacement.with === e2eModule)
      )
      .map(([name]) => name);

    expect(configurationsReplacingAuth).toEqual(['e2e']);
    expect(buildConfigurations['e2e'].fileReplacements).toEqual([
      { replace: 'src/app/auth/browser-auth-providers.ts', with: e2eModule }
    ]);
  });

  it('keeps the production and development builds free of any file replacement', () => {
    expect(buildConfigurations['production'].fileReplacements).toBeUndefined();
    expect(buildConfigurations['development'].fileReplacements).toBeUndefined();
  });

  it('serves the end-to-end configuration from the end-to-end build target only', () => {
    const serveConfigurations = angularJson.projects['inventory-app'].architect.serve.configurations;

    expect(serveConfigurations['e2e'].buildTarget).toBe('inventory-app:build:e2e');
    expect(serveConfigurations['production'].buildTarget).toBe('inventory-app:build:production');
    expect(serveConfigurations['development'].buildTarget).toBe('inventory-app:build:development');
  });

  /**
   * The authority choice in front of the real guard (issue #711).
   *
   * `app.routes.ts` names `MsalGuard`, so the gate reaches every protected route by taking that
   * token's place - the same way the end-to-end build already replaces it. The real guard is still
   * there, under `MSAL_REDIRECT_GUARD`, and still makes every decision the moment there is an
   * authority to use.
   */
  describe('the authority choice in front of the guard', () => {
    const route = {} as ActivatedRouteSnapshot;

    function decide(accounts: number, url: string): unknown {
      TestBed.configureTestingModule({
        providers: [
          SignInAuthorityGuard,
          { provide: MSAL_REDIRECT_GUARD, useValue: { canActivate: () => 'delegated' } },
          {
            provide: MsalService,
            useValue: { instance: { getAllAccounts: () => new Array(accounts).fill({}) } }
          },
          provideRouter([])
        ]
      });

      return TestBed.inject(SignInAuthorityGuard).canActivate(route, { url } as RouterStateSnapshot);
    }

    beforeEach(() => {
      localStorage.clear();
      sessionStorage.clear();
      unconfigureExternalId();
    });

    afterEach(() => {
      unconfigureExternalId();
      TestBed.resetTestingModule();
    });

    it('does not intervene at all with no customer identity tenant configured', () => {
      expect(decide(0, '/reports/daily')).toBe('delegated');
      expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
    });

    it('sends a signed-out visitor who has not chosen yet to the sign-in page', () => {
      configureExternalId();

      const decision = decide(0, '/reports/daily');

      expect(decision).toBeInstanceOf(UrlTree);
      expect(String(decision)).toBe('/sign-in');
    });

    it('records the page that visitor asked for, from the route it refused', () => {
      configureExternalId();

      decide(0, '/reports/daily?machineId=7');

      expect(peekIntendedDestination()).toBe('/reports/daily?machineId=7');
    });

    /**
     * An invite link has already been turned into the tokenless `/invite` by the time any
     * `canActivate` guard runs, so that - and never the token - is what the sign-in returns to.
     */
    it('records an invite acceptance page as the tokenless /invite', () => {
      configureExternalId();

      decide(0, '/invite');

      expect(peekIntendedDestination()).toBe('/invite');
    });

    it('delegates to the real guard once somebody is signed in', () => {
      configureExternalId();

      expect(decide(1, '/reports/daily')).toBe('delegated');
    });

    it('delegates to the real guard once an authority has been chosen', () => {
      configureExternalId();
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

      expect(decide(0, '/reports/daily')).toBe('delegated');
      expect(sessionStorage.getItem(INTENDED_DESTINATION_KEY)).toBeNull();
    });
  });

  /**
   * What the guard and the interceptor ask for follows the remembered authority (issue #711): the
   * two authorities expose `access_as_user` from two different API app registrations, and each
   * bearer scheme on the API validates only its own audience.
   */
  describe('the authority each MSAL configuration is built for', () => {
    beforeEach(() => {
      localStorage.clear();
      // A fresh tab for each case: the authority is snapshotted per tab, so a pin left behind by
      // the previous one would be exactly the thing these tests are about.
      sessionStorage.clear();
      unconfigureExternalId();
    });

    afterEach(() => {
      unconfigureExternalId();
      TestBed.resetTestingModule();
    });

    function interceptorScopes(): string[] | undefined {
      const configuration = MSALInterceptorConfigFactory({ apiBaseUrl: '/api' } as ConfigService);
      return configuration.protectedResourceMap.get('/api/*') as string[] | undefined;
    }

    function guardScopes(): string[] | undefined {
      return (MSALGuardConfigFactory().authRequest as { scopes?: string[] } | undefined)?.scopes;
    }

    it('asks for the workforce audience with nothing configured and nothing chosen', () => {
      expect(guardScopes()).toEqual([apiScopeFor('workforce')]);
      expect(interceptorScopes()).toEqual([apiScopeFor('workforce')]);
    });

    it('asks for the customer identity audience once that authority is the chosen one', () => {
      configureExternalId();
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

      expect(guardScopes()).toContain(apiScopeFor('externalId'));
      expect(interceptorScopes()).toEqual([apiScopeFor('externalId')]);
    });

    it('falls back to the workforce audience when the configuration is cleared under a choice', () => {
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

      expect(interceptorScopes()).toEqual([apiScopeFor('workforce')]);
    });

    /**
     * The three configurations are built by three factories at three different moments - the
     * interceptor's is not constructed until the first HTTP request goes through `HttpClient` -
     * and the choice they derive from is `localStorage`, which every tab of this origin shares.
     * Each one therefore reads the authority through `readSelectedAuthority()`, which answers
     * from this tab's own snapshot: a second tab choosing differently in between must not leave
     * one authority's `PublicClientApplication` asking for the other authority's scopes, which
     * would simply fail to acquire a token.
     *
     * The guard's configuration stands in for the bootstrap here because a real
     * `PublicClientApplication` cannot be constructed under jsdom (no `crypto.subtle`);
     * `MSALInstanceFactory` reads the authority through the same one call, and
     * `selected-authority.spec.ts` pins the snapshot itself.
     */
    it('builds the guard and the interceptor for one authority, whatever another tab does', () => {
      configureExternalId();
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'externalId');

      // Bootstrap: the first configuration built in this tab takes the snapshot.
      expect(guardScopes()).toContain(apiScopeFor('externalId'));

      // Another tab now changes the shared choice, long before the interceptor is constructed.
      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'workforce');

      expect(interceptorScopes()).toEqual([apiScopeFor('externalId')]);
      expect(guardScopes()).toContain(apiScopeFor('externalId'));
    });

    /** The same shared write landing while this tab's redirect is in flight: the snapshot lives in
     * `sessionStorage` and survives the round trip, so the callback page load rebuilds every
     * configuration for the authority that started the journey. */
    it('comes back from a redirect into the authority that started it', () => {
      configureExternalId();
      rememberSelectedAuthority('externalId');

      localStorage.setItem(SELECTED_AUTHORITY_KEY, 'workforce');

      // The callback page load builds everything again, in the same tab.
      expect(readSelectedAuthority()).toBe('externalId');
      expect(guardScopes()).toContain(apiScopeFor('externalId'));
      expect(interceptorScopes()).toEqual([apiScopeFor('externalId')]);
    });
  });

  describe('the end-to-end replacement', () => {
    it('lets the router through instead of redirecting to Entra', () => {
      expect(new E2ETestAuthGuard().canActivate()).toBe(true);
    });

    it('registers no HTTP interceptor, so no bearer token is ever attached', () => {
      expect(provideTokens(e2eBrowserAuthProviders)).not.toContain(HTTP_INTERCEPTORS);
    });

    it('overrides the MSAL guard and services without providing a real MSAL instance', () => {
      const tokens = provideTokens(e2eBrowserAuthProviders);

      expect(tokens).toContain(MsalGuard);
      expect(tokens).toContain(MsalService);
      expect(tokens).toContain(MsalBroadcastService);
      expect(tokens).not.toContain(MSAL_INSTANCE);
    });

    it('reports a deterministic signed-in synthetic account to the application shell', () => {
      const msal = createE2ETestMsalService();

      expect(msal.instance.getActiveAccount()?.name).toBe('E2E Test Actor');
      expect(msal.instance.getAllAccounts()).toHaveLength(1);
    });

    it('satisfies the MSAL application initializer, so the E2E build bootstraps at all', async () => {
      // `app.config.ts` runs `initializeMsal` as an APP_INITIALIZER on every build, including the
      // E2E one, and an initializer that throws aborts bootstrap and renders a blank page - so a
      // member missing from this stub takes the whole browser suite down rather than one test. The
      // real initializer is what asserts that here, so the two cannot drift apart.
      await expect(initializeMsal(createE2ETestMsalService())()).resolves.toBeUndefined();
    });

    it('settles MSAL interaction immediately so the shell does not wait for a redirect', () => {
      const factory = e2eBrowserAuthProviders.find(
        (provider): provider is FactoryProvider =>
          typeof provider === 'object' && 'provide' in provider && provider.provide === MsalBroadcastService
      );
      const observed: InteractionStatus[] = [];

      (factory!.useFactory as () => MsalBroadcastService)().inProgress$.subscribe((status) =>
        observed.push(status)
      );

      expect(observed).toEqual([InteractionStatus.None]);
    });
  });
});
