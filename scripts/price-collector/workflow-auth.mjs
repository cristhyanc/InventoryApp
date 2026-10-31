// OIDC is exchanged only by trusted workflow steps, never by the model's tools.
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

async function jsonResponse(response, label) {
  // Do not include provider bodies: they can contain assertions, application ids or other data.
  if (!response.ok) throw new Error(`${label}: HTTP ${response.status}.`);
  return response.json();
}

export async function collectorToken(env, fetchImpl = fetch) {
  const tenant = env.PRICE_COLLECTOR_TENANT_ID;
  const client = env.PRICE_COLLECTOR_CLIENT_ID;
  const scope = env.PRICE_COLLECTOR_API_SCOPE;
  if (!guid.test(tenant ?? '') || !guid.test(client ?? '') || !/^api:\/\/[^\s]+\/\.default$/.test(scope ?? '')) {
    throw new Error('Configure the collector tenant, client and API application /.default scope.');
  }
  const oidcUrl = new URL(env.ACTIONS_ID_TOKEN_REQUEST_URL);
  if (oidcUrl.protocol !== 'https:' || !env.ACTIONS_ID_TOKEN_REQUEST_TOKEN) throw new Error('GitHub OIDC is unavailable.');
  oidcUrl.searchParams.set('audience', 'api://AzureADTokenExchange');
  const oidc = await jsonResponse(await fetchImpl(oidcUrl, {
    headers: { Authorization: `Bearer ${env.ACTIONS_ID_TOKEN_REQUEST_TOKEN}` },
    redirect: 'error', signal: AbortSignal.timeout(30000),
  }), 'GitHub OIDC');
  if (typeof oidc.value !== 'string' || !oidc.value) throw new Error('GitHub OIDC returned no assertion.');
  const body = new URLSearchParams({ client_id: client, scope, grant_type: 'client_credentials',
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', client_assertion: oidc.value });
  const answer = await jsonResponse(await fetchImpl(`https://login.microsoftonline.com/${tenant}/oauth2/v2.0/token`, {
    method: 'POST', body, redirect: 'error', signal: AbortSignal.timeout(30000),
  }), 'Entra exchange');
  if (typeof answer.access_token !== 'string' || !answer.access_token || Number(answer.expires_in) < 1800) {
    throw new Error('The collector needs an API token valid for at least 30 minutes.');
  }
  return answer.access_token;
}
