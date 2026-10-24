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
 * This loads the real compiled `src/styles.css` into jsdom rather than parsing `styles.scss`, so
 * it exercises the exact selectors the app ships, the same way
 * `stat-card-icon-position.spec.ts` beside it does. jsdom resolves the cascade (which rule matches
 * which element) without computing layout, which is exactly what this needs to check: "does any
 * `width`/padding/border rule apply to this checkbox at all", not "how many pixels wide is it on
 * screen" - that geometry question is answered by the Playwright specs instead (pattern:
 * `e2e/tests/avco-transition-checkbox-layout.e2e.ts` and
 * `e2e/tests/checkbox-radio-global-sizing.e2e.ts`), which run against real layout.
 */
describe('Global input sizing rule excludes checkboxes and radios (issue #874)', () => {
  let styleElement: HTMLStyleElement;

  beforeAll(() => {
    const compiledCss = readFileSync(join(__dirname, '..', '..', 'styles.css'), 'utf8');
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
