import {
  CATALOGUE_MATCH_MIN_SCORE,
  closestCatalogueMatch,
  hasConflict,
  isSuggestableMatch,
  parsePackCount,
  parseSize,
  rankCatalogueCandidates,
  scoreCatalogueNameMatch
} from './catalogue-match';

/**
 * The closest-local-match hint (issue #1353). These tests pin the one scoring/conflict rule
 * directly, independent of the component that reads it.
 */
describe('scoreCatalogueNameMatch', () => {
  it('scores an exact name a perfect match', () => {
    const result = scoreCatalogueNameMatch('Coke Zero', 'Coke Zero');

    expect(result.score).toBe(1);
    expect(hasConflict(result)).toBe(false);
  });

  it('ignores case, extra spaces and punctuation', () => {
    const result = scoreCatalogueNameMatch('COKE  ZERO!!', 'coke, zero.');

    expect(result.score).toBe(1);
  });

  it('ignores word order', () => {
    const result = scoreCatalogueNameMatch('Zero Coke', 'Coke Zero');

    expect(result.score).toBe(1);
  });

  it('scores small wording differences highly without being exact', () => {
    const result = scoreCatalogueNameMatch('Monster Energy Original 500ml', 'Monster Original 500ml');

    expect(result.score).toBeCloseTo(2 / 3, 5);
    expect(result.score).toBeGreaterThanOrEqual(CATALOGUE_MATCH_MIN_SCORE);
    expect(hasConflict(result)).toBe(false);
  });

  it('scores an unrelated name at zero and below the minimum', () => {
    const result = scoreCatalogueNameMatch('Coke Zero 600ml', 'Spring Water 600ml');

    expect(result.score).toBe(0);
    expect(isSuggestableMatch(result)).toBe(false);
  });

  it('treats an empty name as matching nothing, not everything', () => {
    expect(scoreCatalogueNameMatch('', 'Coke Zero').score).toBe(0);
    expect(scoreCatalogueNameMatch('Coke Zero', '').score).toBe(0);
    expect(scoreCatalogueNameMatch('', '').score).toBe(0);
  });

  it('is not suggestable below the documented minimum score', () => {
    const result = scoreCatalogueNameMatch('Coke Zero 600ml', 'Diet Pepsi 600ml');

    expect(result.score).toBeLessThan(CATALOGUE_MATCH_MIN_SCORE);
    expect(isSuggestableMatch(result)).toBe(false);
  });

  it('required case: 375 mL vs 600 mL conflicts on size and is never suggestable', () => {
    const result = scoreCatalogueNameMatch('Coke Zero 375 mL', 'Coke Zero 600 mL');

    expect(result.sizeConflict).toBe(true);
    expect(isSuggestableMatch(result)).toBe(false);
  });

  it('required case: 600ML vs 600 mL is equivalent formatting and is suggestable', () => {
    const result = scoreCatalogueNameMatch('Coke Zero 600ML', 'Coke Zero 600 mL');

    expect(result.sizeConflict).toBe(false);
    expect(result.score).toBe(1);
    expect(isSuggestableMatch(result)).toBe(true);
  });

  it('required case: 1.25L vs 1250ml normalise to the same volume and are suggestable', () => {
    const result = scoreCatalogueNameMatch('Cola 1.25L', 'Cola 1250ml');

    expect(result.sizeConflict).toBe(false);
    expect(result.score).toBe(1);
    expect(isSuggestableMatch(result)).toBe(true);
  });

  it('required case: 24 pack vs x30 conflicts on pack count and is withheld', () => {
    const result = scoreCatalogueNameMatch('Mars Bar 24 pack', 'Mars Bar x30');

    expect(result.packConflict).toBe(true);
    expect(isSuggestableMatch(result)).toBe(false);
  });

  it('does not conflict when only one name states a size', () => {
    const result = scoreCatalogueNameMatch('Coke Zero 600ml', 'Coke Zero');

    expect(result.sizeConflict).toBe(false);
  });

  it('does not conflict when the sizes are of different kinds', () => {
    const result = scoreCatalogueNameMatch('Mars Bar 53g', 'Mars Bar 500ml');

    expect(result.sizeConflict).toBe(false);
  });

  it('does not conflict when only one name states a pack count', () => {
    const result = scoreCatalogueNameMatch('Mars Bar 24 pack', 'Mars Bar');

    expect(result.packConflict).toBe(false);
  });

  it('normalises kilograms to grams and litres to millilitres', () => {
    expect(scoreCatalogueNameMatch('Rice 1kg', 'Rice 1000g').sizeConflict).toBe(false);
    expect(scoreCatalogueNameMatch('Juice 2 litres', 'Juice 2000 ml').sizeConflict).toBe(false);
  });
});

describe('parseSize', () => {
  it('reads a volume in mL, L, litre and liter forms', () => {
    expect(parseSize('Coke Zero 600ml')).toEqual({ kind: 'volume', valueInBaseUnit: 600 });
    expect(parseSize('Coke Zero 600 mL')).toEqual({ kind: 'volume', valueInBaseUnit: 600 });
    expect(parseSize('Cola 1.25L')).toEqual({ kind: 'volume', valueInBaseUnit: 1250 });
    expect(parseSize('Cola 1.25 litre')).toEqual({ kind: 'volume', valueInBaseUnit: 1250 });
  });

  it('reads a weight in g, kg forms', () => {
    expect(parseSize('Mars Bar 53g')).toEqual({ kind: 'weight', valueInBaseUnit: 53 });
    expect(parseSize('Rice 1.5kg')).toEqual({ kind: 'weight', valueInBaseUnit: 1500 });
  });

  it('reads no size from a name stating none', () => {
    expect(parseSize('Mars Bar')).toBeNull();
  });

  it('reads no size from an empty name', () => {
    expect(parseSize('')).toBeNull();
  });
});

describe('parsePackCount', () => {
  it('reads 24pk, 24 pack, x24 and 24 x forms', () => {
    expect(parsePackCount('Mars Bar 24pk')).toBe(24);
    expect(parsePackCount('Mars Bar 24 pack')).toBe(24);
    expect(parsePackCount('Mars Bar x24')).toBe(24);
    expect(parsePackCount('Mars Bar 24 x')).toBe(24);
    expect(parsePackCount('Mars Bar x30')).toBe(30);
  });

  it('reads no pack count from a name stating none', () => {
    expect(parsePackCount('Mars Bar')).toBeNull();
  });

  it('reads no pack count from an empty name', () => {
    expect(parsePackCount('')).toBeNull();
  });
});

describe('rankCatalogueCandidates and closestCatalogueMatch', () => {
  interface Candidate {
    id: number;
    name: string;
  }

  const nameOf = (candidate: Candidate): string => candidate.name;

  it('ranks non-conflicting candidates first, best score first, then by name', () => {
    const candidates: Candidate[] = [
      { id: 1, name: 'Coke Zero 375 mL' }, // conflicts on size
      { id: 2, name: 'Spring Water 600ml' }, // no overlap, score 0
      { id: 3, name: 'Coke Zero 600 mL' } // exact match once size agrees
    ];

    const ranked = rankCatalogueCandidates('Coke Zero 600ml', candidates, nameOf);

    expect(ranked.map((entry) => entry.candidate.id)).toEqual([3, 2, 1]);
    expect(ranked[2].result.sizeConflict).toBe(true);
  });

  it('returns the closest non-conflicting candidate above the minimum score', () => {
    const candidates: Candidate[] = [
      { id: 1, name: 'Coke Zero 375 mL' },
      { id: 2, name: 'Coke Zero 600 mL' }
    ];

    expect(closestCatalogueMatch('Coke Zero 600ml', candidates, nameOf)?.id).toBe(2);
  });

  it('required case: with no other candidate, a conflicting size suggests nothing', () => {
    const candidates: Candidate[] = [{ id: 1, name: 'Coke Zero 600 mL' }];

    expect(closestCatalogueMatch('Coke Zero 375 mL', candidates, nameOf)).toBeNull();
  });

  it('suggests nothing when every candidate scores below the minimum', () => {
    const candidates: Candidate[] = [{ id: 1, name: 'Spring Water 600ml' }];

    expect(closestCatalogueMatch('Coke Zero 600ml', candidates, nameOf)).toBeNull();
  });

  it('suggests nothing with no candidates at all', () => {
    expect(closestCatalogueMatch('Coke Zero 600ml', [], nameOf)).toBeNull();
  });
});
