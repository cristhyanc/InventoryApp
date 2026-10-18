import { Inject, Injectable, InjectionToken } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { IPublicClientApplication, PublicClientApplication, RedirectRequest } from '@azure/msal-browser';
import { AuthorityId, clientIdFor, createMsalConfig, loginRequestFor } from '../auth-config';
import { takeIntendedDestination } from './intended-destination';
import { rememberSelectedAuthority } from './selected-authority';

/** Why a hosted sign-in did not finish, as the sign-in page needs to explain it. */
export type SignInFailureKind = 'cancelled' | 'failed';

/**
 * How an MSAL instance for an authority other than the bootstrapped one is built. It is a provider
 * rather than a `new` inside the service because building a real `PublicClientApplication` is the
 * one thing in this file that reaches the browser's own crypto and storage; everything else about
 * choosing an authority can then be exercised without it.
 */
export type AuthorityClientFactory = (authority: AuthorityId) => Promise<IPublicClientApplication>;

export const AUTHORITY_CLIENT_FACTORY = new InjectionToken<AuthorityClientFactory>('Authority MSAL client factory', {
  providedIn: 'root',
  factory: () => async (authority: AuthorityId) => {
    const client = new PublicClientApplication(createMsalConfig(authority));
    await client.initialize();
    return client;
  }
});

/**
 * The provider error codes that mean "the person changed their mind", rather than "something is
 * wrong". Entra answers a cancelled hosted journey with `access_denied`; MSAL raises
 * `user_cancelled` for an interaction the browser abandoned, and the hosted customer-identity user
 * flows add their own `AADB2C90091` for the Cancel button on a sign-up or sign-in page.
 */
const CANCELLED_CODES = ['access_denied', 'user_cancelled', 'aadb2c90091'];

/**
 * Starting a sign-in against a chosen authority, and reporting one that did not finish
 * (issue #711, design `docs/identity-provider-decision.md` § 9.2 and § 9.3).
 *
 * **One MSAL instance per authority, never a live instance repointed.** MSAL partitions its token
 * cache by client id and authority, so the way to offer two authorities is two instances with two
 * client ids - switching the authority on a running instance is exactly what serves a token issued
 * by one authority out of the other's cache. The instance `browser-auth-providers.ts` bootstrapped
 * is the remembered authority's; choosing the *other* one therefore builds that authority's own
 * instance to make the redirect with, after recording the choice, so the next page load bootstraps
 * the matching instance and completes the redirect inside it.
 *
 * **There is no silent fallback.** A sign-in that cannot complete is reported and retried against
 * the authority that was chosen. This service never tries the other authority, never acquires a
 * token for the other API audience, and never starts a second redirect by itself - automatically
 * retrying a failing authority is how a callback becomes a loop.
 */
@Injectable({ providedIn: 'root' })
export class SignInService {
  /** The last sign-in that did not finish, until the sign-in page takes it. */
  private failure: SignInFailureKind | null = null;

  constructor(
    private readonly msal: MsalService,
    @Inject(AUTHORITY_CLIENT_FACTORY) private readonly createClient: AuthorityClientFactory
  ) {}

  /**
   * Redirects to `authority`'s hosted sign-in page. For `externalId` that is the External ID user
   * flow a human configured, which is where Google is offered: this application sends no provider
   * hint, invents no query parameter and never talks to Google itself (design § 4, journey C).
   */
  async signIn(authority: AuthorityId): Promise<void> {
    this.failure = null;
    rememberSelectedAuthority(authority);

    const destination = takeIntendedDestination();
    const request: RedirectRequest = {
      ...loginRequestFor(authority),
      ...(destination === null ? {} : { redirectStartPage: destination })
    };

    const client = await this.clientFor(authority);
    await client.loginRedirect(request);
  }

  /** Records a redirect that came back refused, cancelled or broken, for the sign-in page to show. */
  report(error: unknown): void {
    this.failure = classifySignInFailure(error);
  }

  /** Reads the recorded failure and clears it, so a later visit to the page is not haunted by it. */
  take(): SignInFailureKind | null {
    const reported = this.failure;
    this.failure = null;
    return reported;
  }

  private async clientFor(authority: AuthorityId): Promise<IPublicClientApplication> {
    const bootstrapped = this.msal.instance;
    return bootstrapped.getConfiguration().auth.clientId === clientIdFor(authority)
      ? bootstrapped
      : this.createClient(authority);
  }
}

/**
 * What a failed sign-in was: somebody changing their mind, or something going wrong. The two are
 * told apart by the provider's own error code, because they need different sentences - "you are
 * not signed in" is reassuring after a Cancel and useless after an outage.
 */
export function classifySignInFailure(error: unknown): SignInFailureKind {
  const code = (error as { errorCode?: unknown } | null)?.errorCode;
  const cancelled = typeof code === 'string' && CANCELLED_CODES.includes(code.trim().toLowerCase());
  return cancelled ? 'cancelled' : 'failed';
}
