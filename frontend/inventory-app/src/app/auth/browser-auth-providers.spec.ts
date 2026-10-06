import { readFileSync } from 'fs';
import { join } from 'path';
import { HTTP_INTERCEPTORS } from '@angular/common/http';
import { ClassProvider, FactoryProvider, Provider } from '@angular/core';
import { MsalBroadcastService, MsalGuard, MsalInterceptor, MsalService, MSAL_INSTANCE } from '@azure/msal-angular';
import { InteractionStatus } from '@azure/msal-browser';
import { browserAuthProviders } from './browser-auth-providers';
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

  it('wires the real MSAL guard, interceptor and instance in the module every build compiles', () => {
    const tokens = provideTokens(browserAuthProviders);

    expect(tokens).toContain(MsalGuard);
    expect(tokens).toContain(MsalService);
    expect(tokens).toContain(MsalBroadcastService);
    expect(tokens).toContain(MSAL_INSTANCE);

    const interceptor = browserAuthProviders.find(
      (provider): provider is ClassProvider =>
        typeof provider === 'object' && 'provide' in provider && provider.provide === HTTP_INTERCEPTORS
    );
    expect(interceptor?.useClass).toBe(MsalInterceptor);
    expect(interceptor?.multi).toBe(true);
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
