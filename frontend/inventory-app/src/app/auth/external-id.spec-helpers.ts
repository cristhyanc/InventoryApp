import { ExternalIdSettings, externalIdSettings } from '../auth-config';

/**
 * A deployment whose customer identity tenant a human has configured (issue #711).
 *
 * Tests must not depend on whatever identifiers `externalIdSettings` holds in source control, so a
 * test that needs the second authority writes these test identifiers into that one object, and a
 * test that needs the workforce-only state clears it with `unconfigureExternalId`. That is also
 * what proves the configuration is the only thing that turns the second authority on: nothing else
 * in the application has to change for Google sign-in to appear.
 */
export const CONFIGURED_EXTERNAL_ID: ExternalIdSettings = {
  instance: 'https://contoso.ciamlogin.com/',
  tenantId: 'cccccccc-0000-4000-8000-00000000000c',
  clientId: '11111111-0000-4000-8000-000000000011',
  apiClientId: '22222222-0000-4000-8000-000000000022'
};

const UNCONFIGURED: ExternalIdSettings = { instance: '', tenantId: '', clientId: '', apiClientId: '' };

/** Configures the customer identity tenant for the test about to run. */
export function configureExternalId(settings: Partial<ExternalIdSettings> = {}): void {
  Object.assign(externalIdSettings, CONFIGURED_EXTERNAL_ID, settings);
}

/** Clears the customer identity tenant: the unconfigured (rollback) state, no second authority. */
export function unconfigureExternalId(): void {
  Object.assign(externalIdSettings, UNCONFIGURED);
}
