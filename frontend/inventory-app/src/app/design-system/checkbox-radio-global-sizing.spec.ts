import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The global `input, select, textarea` rule in `src/styles.scss` must no longer size
 * `input[type=checkbox]` or `input[type=radio]` (issue #874): a flex label otherwise stretched
 * the box across the row and drew its tick far from the label text (issue #872). Every other
 * input, select and textarea must keep the full width, padding and border that rule has always
 * given them, and the global focus-visible/disabled rules - which target bare `input` and so were
 * never scoped to this change - must still reach checkboxes and radios.
 *
 * Both halves of that read the real compiled `src/styles.css`, not `styles.scss`, so they exercise
 * the exact selectors the app ships, the same way `stat-card-icon-position.spec.ts` beside it does.
 * They read it in two different ways on purpose, because neither alone is enough:
 *
 *  - The first describe loads it into jsdom and asks `getComputedStyle` whether any rule reaches a
 *    checkbox at all. jsdom applies every matching rule in **source order and ignores specificity
 *    entirely**, so this is a sound way to ask "does a width/padding/border declaration match this
 *    element" and an unsound way to ask "which of two matching rules wins" - that is what the
 *    second describe is for, and why it is not written with `getComputedStyle` too.
 *  - The second describe computes the selectors' specificity, because keeping the pre-#874 cascade
 *    for ordinary inputs is a specificity property and nothing else: the compiled utilities sit
 *    *earlier* in the stylesheet than this base rule, so a single-class `.w-24` overrides it only
 *    as long as the base rule stays element-weight.
 *
 * Neither answers "how many pixels wide is it on screen". That geometry question belongs to the
 * Playwright specs (`e2e/tests/avco-transition-checkbox-layout.e2e.ts` and
 * `e2e/tests/checkbox-radio-global-sizing.e2e.ts`), which run against real layout in a real browser.
 */
const compiledCss = readFileSync(join(__dirname, '..', '..', 'styles.css'), 'utf8');

/**
 * The same stylesheet with its comments removed, for the selector assertions below. The comment
 * above the global rule in `styles.scss` quotes the rejected `:not()` form in prose to explain why
 * it was rejected, and Tailwind copies the comment through into `styles.css`, so a plain substring
 * search over the file cannot tell a selector from an explanation of one.
 */
const compiledSelectors = compiledCss.replace(/\/\*[\s\S]*?\*\//g, '');

describe('Global input sizing rule excludes checkboxes and radios (issue #874)', () => {
  let styleElement: HTMLStyleElement;

  beforeAll(() => {
    styleElement = document.createElement('style');
    styleElement.textContent = compiledCss;
    document.head.appendChild(styleElement);
  });

  afterAll(() => {
    styleElement.remove();
  });

  function appendInput(type: string): HTMLInputElement {
    const input = document.createElement('input');
    input.type = type;
    document.body.appendChild(input);
    return input;
  }

  afterEach(() => {
    document.body.innerHTML = '';
  });

  it.each(['checkbox', 'radio'])('gives %s no width, padding or border from the text-input rule', (type) => {
    const input = appendInput(type);
    const style = getComputedStyle(input);

    expect(style.width).not.toBe('100%');
    expect(style.paddingLeft).not.toBe('0.75rem');
    expect(style.borderLeftWidth).not.toBe('1px');
  });

  it.each(['text', 'number', 'email'])('still gives a %s input the full text-input sizing', (type) => {
    const input = appendInput(type);
    const style = getComputedStyle(input);

    expect(style.width).toBe('100%');
    expect(style.paddingLeft).toBe('0.75rem');
    expect(style.paddingTop).toBe('0.5rem');
  });

  it('still gives select and textarea the full text-input sizing', () => {
    const select = document.createElement('select');
    document.body.appendChild(select);
    const textarea = document.createElement('textarea');
    document.body.appendChild(textarea);

    expect(getComputedStyle(select).width).toBe('100%');
    expect(getComputedStyle(textarea).width).toBe('100%');
  });

  it.each(['checkbox', 'radio'])('still gives a disabled %s the global disabled styling', (type) => {
    const input = appendInput(type);
    input.disabled = true;
    const style = getComputedStyle(input);

    expect(style.opacity).toBe('0.5');
    expect(style.cursor).toBe('not-allowed');
  });

  it.each(['checkbox', 'radio'])('still gives a focused %s the global focus-visible outline', (type) => {
    const input = appendInput(type);
    input.focus();
    const style = getComputedStyle(input);

    expect(style.outlineStyle).toBe('solid');
    expect(style.outlineWidth).toBe('2px');
  });
});

interface Specificity {
  ids: number;
  classes: number;
  types: number;
}

const ZERO: Specificity = { ids: 0, classes: 0, types: 0 };

/** The identifier characters these selectors use, including the leading `-` of `-moz-placeholder`. */
const IDENTIFIER = /[-\w\\]/;

/**
 * Selectors Level 4 specificity of one compound selector, over the subset of syntax the global
 * form-control rules and the Tailwind utilities that override them actually use: type selectors,
 * `.class`, `#id`, `[attribute]`, pseudo-classes, pseudo-elements, and the three functional
 * pseudo-classes whose weight is not their own - `:where()`, which contributes **zero**, and
 * `:not()`/`:is()`/`:has()`, which contribute their most specific argument.
 *
 * It exists because this is the one property the #874 exclusion had to preserve and the one thing
 * neither jsdom nor a text comparison can state: `getComputedStyle` resolves the cascade by source
 * order alone, and "the selector text changed" says nothing about whether it still loses to a
 * utility class. The `describes the weight of the selectors it is used on` case below checks the
 * function itself against hand-computed values, including the regression it exists to catch.
 */
function specificity(selector: string): Specificity {
  const source = selector.trim();
  let ids = 0;
  let classes = 0;
  let types = 0;
  let index = 0;

  const readIdentifier = (): string => {
    const start = index;
    while (index < source.length && IDENTIFIER.test(source[index])) {
      index += 1;
    }
    return source.slice(start, index);
  };

  /** The text inside the parentheses starting at `index`, leaving `index` just past the `)`. */
  const readArguments = (): string => {
    const start = index + 1;
    let depth = 0;
    while (index < source.length) {
      if (source[index] === '(') {
        depth += 1;
      } else if (source[index] === ')') {
        depth -= 1;
        if (depth === 0) {
          const argumentList = source.slice(start, index);
          index += 1;
          return argumentList;
        }
      }
      index += 1;
    }
    throw new Error(`Unbalanced "(" in selector: ${selector}`);
  };

  while (index < source.length) {
    const character = source[index];

    if (character === '#') {
      index += 1;
      readIdentifier();
      ids += 1;
    } else if (character === '.') {
      index += 1;
      readIdentifier();
      classes += 1;
    } else if (character === '[') {
      while (index < source.length && source[index] !== ']') {
        index += 1;
      }
      index += 1;
      classes += 1;
    } else if (character === ':') {
      const isPseudoElement = source[index + 1] === ':';
      index += isPseudoElement ? 2 : 1;
      const name = readIdentifier().toLowerCase();
      const argumentList = source[index] === '(' ? readArguments() : null;

      if (isPseudoElement) {
        types += 1;
      } else if (name === 'where') {
        // Zero, whatever is inside it. This is the whole point of the #874 selector.
      } else if (argumentList !== null && (name === 'not' || name === 'is' || name === 'has')) {
        const strongest = splitSelectorList(argumentList).map(specificity).reduce(higherOf, ZERO);
        ids += strongest.ids;
        classes += strongest.classes;
        types += strongest.types;
      } else {
        classes += 1;
      }
    } else if (IDENTIFIER.test(character)) {
      readIdentifier();
      types += 1;
    } else {
      // A combinator, whitespace or `*`: no weight of its own.
      index += 1;
    }
  }

  return { ids, classes, types };
}

/** Splits a selector list on its top-level commas, ignoring commas inside `(…)` or `[…]`. */
function splitSelectorList(selectorList: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = '';

  for (const character of selectorList) {
    if (character === '(' || character === '[') {
      depth += 1;
    } else if (character === ')' || character === ']') {
      depth -= 1;
    }

    if (character === ',' && depth === 0) {
      parts.push(current);
      current = '';
      continue;
    }

    current += character;
  }

  parts.push(current);
  return parts.map((part) => part.trim()).filter((part) => part.length > 0);
}

function compareSpecificity(left: Specificity, right: Specificity): number {
  return left.ids - right.ids || left.classes - right.classes || left.types - right.types;
}

function higherOf(left: Specificity, right: Specificity): Specificity {
  return compareSpecificity(left, right) >= 0 ? left : right;
}

/** The position of a rule's selector list in the compiled stylesheet, which decides a tie. */
function sourceIndexOf(selectorList: string): number {
  const position = compiledSelectors.indexOf(`${selectorList} {`);
  if (position < 0) {
    throw new Error(`The compiled stylesheet holds no rule for "${selectorList}"`);
  }
  return position;
}

/**
 * The cascade the #874 exclusion had to leave exactly as it was for every input that is not a
 * checkbox or a radio (the issue's first acceptance criterion, and its exclusion of "changing text
 * input/select/textarea styles").
 *
 * `input:not([type='checkbox']):not([type='radio'])` would have been a silent app-wide restyle: two
 * attribute selectors outweigh one class, so the global `width: 100%`, padding and border would
 * have started beating the single-class utilities that have always overridden them. All three of
 * the overrides asserted here are real call sites at this commit - Take Inventory's counted-stock
 * input (`class="w-24"`), the report filters' date inputs (`class="… w-auto"`) and a purchase line's
 * invalid price (`[class.border-md-danger]`) - and none of them is in this issue's scope to change.
 */
describe('Global input rule keeps its pre-#874 weight for ordinary inputs (issue #874)', () => {
  const excludedInput = "input:where(:not([type='checkbox']):not([type='radio']))";

  /**
   * The global form-control rules as the stylesheet actually ships them - `input…, select…,
   * textarea…` - rather than as this file expects them. The specificity assertions below are only
   * worth anything while they read the shipped selector, not a copy of it.
   */
  const shippedGlobalRules = Array.from(
    compiledSelectors.matchAll(/^(input[^{}\n]*,\s*select[^{}\n]*,\s*textarea[^{}\n]*) \{/gm)
  ).map((match) => match[1]);

  /** The `input …` part of the shipped rule whose `select` part is `select` + `suffix`. */
  function shippedInputSelector(suffix: string): string {
    const matching = shippedGlobalRules.filter((rule) => splitSelectorList(rule)[1] === `select${suffix}`);
    if (matching.length !== 1) {
      throw new Error(`Expected one shipped global rule for "select${suffix}", found ${matching.length}`);
    }
    return splitSelectorList(matching[0])[0];
  }

  const expectedGlobalRules = [
    `${excludedInput}, select, textarea`,
    `${excludedInput}::-moz-placeholder, select::-moz-placeholder, textarea::-moz-placeholder`,
    `${excludedInput}::placeholder, select::placeholder, textarea::placeholder`,
    `${excludedInput}:focus, select:focus, textarea:focus`
  ];

  it('describes the weight of the selectors it is used on', () => {
    // Hand-computed, so a bug in the helper cannot quietly validate a regression.
    expect(specificity('input')).toEqual({ ids: 0, classes: 0, types: 1 });
    expect(specificity('.w-24')).toEqual({ ids: 0, classes: 1, types: 0 });
    expect(specificity('input:focus')).toEqual({ ids: 0, classes: 1, types: 1 });
    expect(specificity('input::placeholder')).toEqual({ ids: 0, classes: 0, types: 2 });
    expect(specificity("input[type='checkbox']")).toEqual({ ids: 0, classes: 1, types: 1 });
    // The rejected form, and exactly why it was rejected: heavier than a single class.
    expect(specificity("input:not([type='checkbox']):not([type='radio'])")).toEqual({
      ids: 0,
      classes: 2,
      types: 1
    });
  });

  it('ships the `:where()` form of the exclusion and not the bare `:not()` one', () => {
    expect(shippedGlobalRules).toEqual(expectedGlobalRules);

    // The regression guard: no rule anywhere in the compiled stylesheet qualifies `input` with a
    // bare, weight-bearing type exclusion.
    expect(compiledSelectors).not.toContain('input:not([type=');
  });

  it.each([
    ['', 'input'],
    ['::-moz-placeholder', 'input::-moz-placeholder'],
    ['::placeholder', 'input::placeholder'],
    [':focus', 'input:focus']
  ])('weighs the shipped `select%s` rule exactly as the pre-#874 `%s` selector did', (suffix, before) => {
    expect(specificity(shippedInputSelector(suffix))).toEqual(specificity(before));
  });

  it.each([
    ['.w-24', 'Take Inventory counted stock', 'width: 6rem;'],
    ['.w-auto', 'report filter dates', 'width: auto;'],
    ['.border-md-danger', 'invalid purchase price', '--tw-border-opacity: 1;']
  ])('lets the single-class %s utility (%s) keep beating the global sizing rule', (utility, _call, declaration) => {
    expect(compiledSelectors).toContain(`${utility} {\n  ${declaration}`);

    const base = specificity(shippedInputSelector(''));
    expect(compareSpecificity(specificity(utility), base)).toBeGreaterThan(0);

    // The utilities are compiled *before* this base rule, so specificity is the only thing that
    // makes them win; a tie would hand it to the base rule instead.
    expect(sourceIndexOf(utility)).toBeLessThan(sourceIndexOf(shippedGlobalRules[0]));
  });

  it('still lets the global focus border outrank the danger border, exactly as before #874', () => {
    // Unchanged behaviour, stated rather than assumed: `input:focus` has always outweighed a
    // single-class border utility, so a focused invalid price input has always shown the md-info
    // focus border and shows it still. The danger border is what it displays when not focused.
    const focusRule = specificity(shippedInputSelector(':focus'));

    expect(compareSpecificity(focusRule, specificity('.border-md-danger'))).toBeGreaterThan(0);
    expect(compareSpecificity(focusRule, specificity('input:focus'))).toBe(0);
  });

  it('leaves the global disabled and focus-visible rules on bare `input`', () => {
    // Checkboxes and radios must keep these (the issue's second acceptance criterion), which is
    // only true while these selectors stay unqualified.
    expect(compiledSelectors).toContain('input:disabled');
    expect(compiledSelectors).toContain('input:focus-visible');
    expect(compiledSelectors).not.toContain(`${excludedInput}:disabled`);
    expect(compiledSelectors).not.toContain(`${excludedInput}:focus-visible`);
  });
});
