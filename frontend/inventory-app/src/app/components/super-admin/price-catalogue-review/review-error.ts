/**
 * The one place the Super Admin review reads a refusal's own sentence (issue #928).
 *
 * Every rule on this surface is the server's - the freshness limit, the price band, the identity
 * comparison, which suggestions may be decided - so what a refused decision shows is the sentence
 * the API returned rather than a second copy of the rule. A `400` and a `409` here both carry one
 * (`DomainExceptionHandler`), and a response that carries none falls back to the caller's own
 * fallback instead of leaving the reviewer with nothing.
 */
export function extractReviewErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: unknown } | undefined)?.error;

  if (typeof body === 'string' && body.trim() !== '') {
    return body;
  }

  const problem = body as { message?: string; title?: string; detail?: string } | undefined;

  return problem?.message ?? problem?.detail ?? problem?.title ?? fallback;
}

/**
 * Whether a refusal means the page is looking at state that has moved: a listing somebody else
 * decided, a suggestion already accepted, or a key nothing is waiting under any more.
 *
 * The page re-reads the queue when it does, because the alternative is a reviewer deciding against a
 * screen that is no longer true.
 */
export function isStaleReviewState(err: unknown): boolean {
  const status = (err as { status?: unknown } | undefined)?.status;

  return status === 404 || status === 409;
}
