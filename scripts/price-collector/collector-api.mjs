// The one way the collector scripts talk to Restockroo (issue #926, parent #817).
//
// **Two environment variables and nothing else.** The API base URL and the collector's app-only
// bearer token come from the environment the scheduled workflow sets (#929), never from an argument,
// a file in the repository or a default: a token in a command line is a token in a process list, and
// a default base URL is a production URL somebody forgot to override. Neither value is ever printed:
// every message this module raises names the method and the route, with any occurrence of the token
// or the base URL redacted on the way out.
//
// **It fails closed.** A non-2xx answer, an answer that is not JSON and a request that never
// connected are all one thing - the call did not succeed - and each raises an `ApiError` rather than
// returning something a caller could mistake for work. See `docs/architecture.md`
// § The price collector's app-only path for what the API does with the token.

/** The environment variable holding the Restockroo API base URL, for example `https://api.example`. */
export const API_BASE_URL_ENV = 'PRICE_COLLECTOR_API_BASE_URL';

/** The environment variable holding the collector's app-only bearer token. Never printed. */
export const API_TOKEN_ENV = 'PRICE_COLLECTOR_API_TOKEN';

const USER_AGENT = 'inventoryapp-price-collector';
const DEFAULT_TIMEOUT_MS = 60000;
const MAX_REPORTED_BODY = 300;

/** The invocation itself is unusable: a missing or malformed setting, or a refused argument. */
export class ConfigurationError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ConfigurationError';
  }
}

/** One call did not succeed. `status` is the HTTP status, or `null` when nothing answered. */
export class ApiError extends Error {
  constructor(message, { status = null } = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/**
 * The API this run talks to, from the environment.
 *
 * @throws {ConfigurationError} Either variable is missing, empty, or not an absolute http(s) URL.
 */
export function resolveApi(env) {
  const rawBaseUrl = String(env?.[API_BASE_URL_ENV] ?? '').trim();
  if (rawBaseUrl === '') {
    throw new ConfigurationError(`${API_BASE_URL_ENV} is not set. The collector reads the API base URL from it.`);
  }

  let parsed;
  try {
    parsed = new URL(rawBaseUrl);
  } catch {
    throw new ConfigurationError(`${API_BASE_URL_ENV} must be an absolute http(s) URL.`);
  }
  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
    throw new ConfigurationError(`${API_BASE_URL_ENV} must be an absolute http(s) URL.`);
  }

  const token = String(env?.[API_TOKEN_ENV] ?? '').trim();
  if (token === '') {
    throw new ConfigurationError(`${API_TOKEN_ENV} is not set. The collector reads its bearer token from it.`);
  }

  return { baseUrl: `${parsed.origin}${parsed.pathname.replace(/\/+$/, '')}`, token };
}

/** Replaces the token and the base URL with `[redacted]` wherever they appear in text to be printed. */
export function redactorFor(api) {
  const secrets = [api?.token, api?.baseUrl].filter((value) => typeof value === 'string' && value !== '');
  return (text) => {
    let safe = String(text ?? '');
    for (const secret of secrets) safe = safe.split(secret).join('[redacted]');
    return safe;
  };
}

const compact = (value) => String(value ?? '').replace(/\s+/g, ' ').trim().slice(0, MAX_REPORTED_BODY);

/**
 * One request, answered as `{ status, body }` only when it succeeded.
 *
 * @throws {ApiError} The request did not connect, timed out, answered a non-2xx status, or answered
 * something that is not JSON. The message names the method and the route and never a credential.
 */
export async function requestJson({ api, method, path, body = null, fetchImpl = fetch, timeoutMs = DEFAULT_TIMEOUT_MS }) {
  const redact = redactorFor(api);
  const route = `${method} ${path}`;
  const headers = {
    Accept: 'application/json',
    Authorization: `Bearer ${api.token}`,
    'User-Agent': USER_AGENT,
  };
  if (body !== null) headers['Content-Type'] = 'application/json';

  let response;
  try {
    response = await fetchImpl(`${api.baseUrl}${path}`, {
      method,
      headers,
      body: body === null ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (error) {
    const timedOut = error?.name === 'TimeoutError' || error?.name === 'AbortError';
    const detail = timedOut ? `no answer within ${timeoutMs} ms` : `the request did not complete (${compact(error?.message)})`;
    throw new ApiError(redact(`${route}: ${detail}.`));
  }

  const raw = await response.text().catch(() => '');

  if (!response.ok) {
    const detail = raw === '' ? '' : ` ${compact(raw)}`;
    throw new ApiError(redact(`${route}: the API answered HTTP ${response.status}.${detail}`), {
      status: response.status,
    });
  }

  if (raw === '') return { status: response.status, body: null };

  try {
    return { status: response.status, body: JSON.parse(raw) };
  } catch (error) {
    throw new ApiError(redact(`${route}: the answer was not JSON (${compact(error?.message)}).`), {
      status: response.status,
    });
  }
}
