/**
 * Resolution of the API base URL. Kept free of Angular so that `ConfigService` — the single
 * source of truth for the API base URL — and the framework-free spec can both use it.
 */

/** Dev-server proxy path; `proxy.conf.json` forwards it to the local API. */
export const LOCAL_API_BASE_URL = '/api';

const LOCAL_HOSTNAMES = ['localhost', '127.0.0.1', '[::1]', '::1'];

export function isLocalDevelopmentHost(hostname: string): boolean {
  return LOCAL_HOSTNAMES.includes(hostname.toLowerCase());
}

/**
 * Local development always calls the local API through the dev proxy, so nobody has to edit
 * the tracked `assets/config.json` when switching between local work and a deployment. Any
 * other origin is a deployed one and uses the configured API URL, falling back to the proxy
 * path when that configuration is missing or unusable.
 */
export function resolveApiBaseUrl(
  hostname: string,
  config: { apiBaseUrl?: string | null } | null | undefined
): string {
  if (isLocalDevelopmentHost(hostname)) return LOCAL_API_BASE_URL;

  const configured = config?.apiBaseUrl?.trim().replace(/\/+$/, '');
  return configured ? configured : LOCAL_API_BASE_URL;
}
