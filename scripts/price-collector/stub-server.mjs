// Test support for the collector scripts (issue #926, parent #817): a local HTTP server the
// get-work and submit-results tests run against, so those tests exercise the real request the real
// `fetch` makes - the method, the route, the bearer header and the JSON body - without ever calling
// Restockroo or a supplier.
//
// Nothing in the collector, the API, the database or any report reads this file: it is loaded only by
// `*.test.mjs` beside it. It is not a `*.test.mjs` file itself, so `node --test` never runs it as a
// suite.
import { createServer } from 'node:http';

/**
 * Starts a stub API on an ephemeral loopback port.
 *
 * `routes` is keyed by `"<METHOD> <path>"`, with `"<METHOD> *"` as a fallback. A value is either
 * `{ status, body }` or a function `(request, callIndexForThisRoute) => { status, body }`, so a test
 * can answer the second call to one route differently from the first. An unrouted request is
 * answered `404` and recorded like any other, which is what makes a stray call visible.
 */
export async function startStubServer(routes) {
  const requests = [];

  const server = createServer((request, response) => {
    const chunks = [];
    request.on('data', (chunk) => chunks.push(chunk));
    request.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8');
      const record = {
        method: request.method,
        path: request.url,
        key: `${request.method} ${request.url}`,
        authorization: request.headers.authorization ?? null,
        body: raw === '' ? null : JSON.parse(raw),
      };
      requests.push(record);

      const route = routes[record.key] ?? routes[`${record.method} *`];
      const callIndex = requests.filter((entry) => entry.key === record.key).length - 1;
      const answer = (typeof route === 'function' ? route(record, callIndex) : route) ?? {
        status: 404,
        body: { error: 'no stub route' },
      };

      response.writeHead(answer.status, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify(answer.body ?? {}));
    });
  });

  await new Promise((started) => server.listen(0, '127.0.0.1', started));
  const { port } = server.address();

  return {
    baseUrl: `http://127.0.0.1:${port}`,
    requests,
    bodiesFor: (key) => requests.filter((entry) => entry.key === key).map((entry) => entry.body),
    close: () => new Promise((closed) => server.close(closed)),
  };
}
