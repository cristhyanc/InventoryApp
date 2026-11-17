/**
 * The closest-local-match hint for the catalogue review screen (issue #1353).
 *
 * This is a display-only suggestion, never a decision: `CatalogueReviewComponent` only reads it to
 * show "Closest match: …" and a "Link to this" button, and pressing that button does exactly what
 * choosing the row from the dropdown does — set the action to `LinkExisting` and name that row. No
 * function here chooses, links or sends anything; the server still links only the row the operator
 * names (AGENTS.md / issue #738).
 *
 * The rule has two independent parts:
 * - A **name score** in `[0, 1]`: the Jaccard similarity of the two names' word sets once each name
 *   is lower-cased, stripped of punctuation, and stripped of the size/pack-count phrase it states (so
 *   that phrase does not drown out the rest of the name, and so differently-formatted equal sizes —
 *   `600ML` vs `600 mL` — score the same as no size at all). Comparing sets rather than sequences is
 *   what makes word order and small wording differences (`Monster Energy Original 500ml` vs
 *   `Monster Original 500ml`) score highly without being an exact match.
 * - A **size/pack conflict**: independent of the score, because a high-scoring name can still name a
 *   different size or pack of the same product. A volume is normalised to millilitres (`1.25L` =
 *   `1250 mL`) and a weight to grams, so `600ML` and `600 mL` compare equal while `375 mL` and
 *   `600 mL` conflict. A pack count is read from `24pk`, `24 pack`, `x24` or `24 x`. Two names
 *   conflict on size only when both state a size of the *same kind* (both volumes, or both weights)
 *   and the values differ, and conflict on pack only when both state a pack count and the counts
 *   differ — a name that states no size, or no pack count, never conflicts on that part.
 *
 * `isSuggestableMatch` is the one place the minimum score and the conflict rule are combined: a
 * conflicting candidate is never suggestable, whatever its score, and a non-conflicting candidate is
 * suggestable only once its score reaches {@link CATALOGUE_MATCH_MIN_SCORE}. A conflicting or
 * below-threshold candidate is still a valid manual choice — this module only decides what gets the
 * one-tap hint, never what the Link to dropdown lists.
 */

/** The minimum name score a non-conflicting candidate must reach to be offered as the closest match. */
export const CATALOGUE_MATCH_MIN_SCORE = 0.5;

/** A size read from a name, normalised to one base unit so differently-formatted equal sizes compare equal. */
export interface CatalogueParsedSize {
  kind: 'volume' | 'weight';
  /** Millilitres for a volume, grams for a weight. */
  valueInBaseUnit: number;
}

/** What comparing one provider name against one local name found. */
export interface CatalogueMatchResult {
  /** Jaccard similarity of the two names' remaining words, in `[0, 1]`, once size/pack is stripped. */
  score: number;
  /** Both names state a size of the same kind (volume or weight) and the values differ. */
  sizeConflict: boolean;
  /** Both names state a pack count and the counts differ. */
  packConflict: boolean;
}

const VOLUME_REGEX =
  /\b(\d+(?:\.\d+)?)\s*(ml|millilitres?|milliliters?|l|litres?|liters?)\b/;
const WEIGHT_REGEX = /\b(\d+(?:\.\d+)?)\s*(kg|kilograms?|g|grams?)\b/;
const PACK_REGEX = /\bx\s*(\d+)\b|\b(\d+)\s*x\b|\b(\d+)\s*(?:pk|packs?)\b/;

/**
 * Lower-cases the name and strips punctuation to a space, keeping only the decimal point of a
 * number (`1.25`) - a sentence-ending period is punctuation like any other, so it is only kept
 * between two digits.
 */
function clean(name: string): string {
  const punctuationToSpace = name.toLowerCase().replace(/[^a-z0-9.\s]/g, ' ');
  const onlyDecimalPoints = punctuationToSpace.replace(/(?<!\d)\.(?!\d)/g, ' ');
  return onlyDecimalPoints.replace(/\s+/g, ' ').trim();
}

function volumeInMl(value: number, unit: string): number {
  return unit === 'ml' || unit.startsWith('milli') ? value : value * 1000;
}

function weightInG(value: number, unit: string): number {
  return unit === 'g' || unit.startsWith('gram') ? value : value * 1000;
}

/** The size stated in `cleaned`, and `cleaned` with that phrase removed. */
function extractSize(cleaned: string): { size: CatalogueParsedSize | null; remainder: string } {
  const volume = VOLUME_REGEX.exec(cleaned);
  if (volume) {
    return {
      size: { kind: 'volume', valueInBaseUnit: volumeInMl(Number.parseFloat(volume[1]), volume[2]) },
      remainder: cleaned.replace(volume[0], ' ')
    };
  }

  const weight = WEIGHT_REGEX.exec(cleaned);
  if (weight) {
    return {
      size: { kind: 'weight', valueInBaseUnit: weightInG(Number.parseFloat(weight[1]), weight[2]) },
      remainder: cleaned.replace(weight[0], ' ')
    };
  }

  return { size: null, remainder: cleaned };
}

/** The pack count stated in `cleaned`, and `cleaned` with that phrase removed. */
function extractPackCount(cleaned: string): { packCount: number | null; remainder: string } {
  const match = PACK_REGEX.exec(cleaned);
  if (!match) return { packCount: null, remainder: cleaned };

  const digits = match[1] ?? match[2] ?? match[3];
  return { packCount: Number.parseInt(digits, 10), remainder: cleaned.replace(match[0], ' ') };
}

/** The size a name states (volume or weight, normalised), or `null` when it states none. */
export function parseSize(name: string): CatalogueParsedSize | null {
  return extractSize(clean(name)).size;
}

/** The pack count a name states (`24pk`, `24 pack`, `x24`, `24 x`), or `null` when it states none. */
export function parsePackCount(name: string): number | null {
  return extractPackCount(clean(name)).packCount;
}

/** The name's words once cleaned and with its size/pack phrase removed, for scoring. */
function scoringTokens(name: string): string[] {
  const afterSize = extractSize(clean(name)).remainder;
  const afterPack = extractPackCount(afterSize).remainder;
  return afterPack.split(/\s+/).filter((token) => token.length > 0);
}

/** The Jaccard similarity of two word sets: the empty name matches nothing, not everything. */
function jaccard(left: string[], right: string[]): number {
  if (left.length === 0 || right.length === 0) return 0;

  const leftSet = new Set(left);
  const rightSet = new Set(right);
  let intersection = 0;
  for (const token of leftSet) {
    if (rightSet.has(token)) intersection++;
  }

  const union = new Set([...leftSet, ...rightSet]).size;
  return intersection / union;
}

/** Compares one provider name against one local name. The one documented scoring/conflict rule. */
export function scoreCatalogueNameMatch(providerName: string, localName: string): CatalogueMatchResult {
  const providerSize = parseSize(providerName);
  const localSize = parseSize(localName);
  const sizeConflict =
    providerSize != null &&
    localSize != null &&
    providerSize.kind === localSize.kind &&
    providerSize.valueInBaseUnit !== localSize.valueInBaseUnit;

  const providerPack = parsePackCount(providerName);
  const localPack = parsePackCount(localName);
  const packConflict = providerPack != null && localPack != null && providerPack !== localPack;

  return {
    score: jaccard(scoringTokens(providerName), scoringTokens(localName)),
    sizeConflict,
    packConflict
  };
}

/** Whether a result names a size or pack conflict, whatever its score. */
export function hasConflict(result: CatalogueMatchResult): boolean {
  return result.sizeConflict || result.packConflict;
}

/** Whether a result is strong enough, and conflict-free enough, to offer as the one-tap suggestion. */
export function isSuggestableMatch(result: CatalogueMatchResult): boolean {
  return !hasConflict(result) && result.score >= CATALOGUE_MATCH_MIN_SCORE;
}

/** One candidate scored against the provider name it might be the closest local match for. */
export interface RankedCatalogueCandidate<T> {
  candidate: T;
  result: CatalogueMatchResult;
}

/**
 * `candidates` scored against `providerName` and ordered the way the Link to dropdown lists them:
 * every non-conflicting candidate first, each group ordered by score (best first) then by name.
 */
export function rankCatalogueCandidates<T>(
  providerName: string,
  candidates: readonly T[],
  nameOf: (candidate: T) => string
): RankedCatalogueCandidate<T>[] {
  return candidates
    .map((candidate) => ({ candidate, result: scoreCatalogueNameMatch(providerName, nameOf(candidate)) }))
    .sort((left, right) => {
      const leftConflict = hasConflict(left.result);
      const rightConflict = hasConflict(right.result);
      if (leftConflict !== rightConflict) return leftConflict ? 1 : -1;
      if (left.result.score !== right.result.score) return right.result.score - left.result.score;
      return nameOf(left.candidate).localeCompare(nameOf(right.candidate));
    });
}

/** The single closest candidate worth a one-tap suggestion, or `null` when none reaches the bar. */
export function closestCatalogueMatch<T>(
  providerName: string,
  candidates: readonly T[],
  nameOf: (candidate: T) => string
): T | null {
  const best = rankCatalogueCandidates(providerName, candidates, nameOf)[0];
  return best && isSuggestableMatch(best.result) ? best.candidate : null;
}
