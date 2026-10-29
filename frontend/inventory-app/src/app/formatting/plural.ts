/**
 * `1 machine`, `3 machines`: a count with its noun, for the sentences the Dashboard and its
 * performance card write. Shared so the two do not each carry their own copy of the rule.
 */
export function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}
