import type { Locator, Page } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * The New Product Name field suggesting the shared master product catalogue, in a real browser
 * (issue #1052's "browser check at desktop and 390 px with two size variants of one product").
 *
 * These are browser tests because jsdom answers none of what the criterion asks. The Jest specs
 * beside the components already assert the stream, the state machine, the prefill, the opt-out and
 * the keyboard against a fake service, but a class is not a measurement and a jsdom event is not a
 * finger: "both size variants are told apart on screen", "the row can be taken by touch", "the open
 * list does not push the page sideways at 390 px" and "the panel stays inside the viewport" are only
 * answerable against real laid-out boxes with the application's compiled Tailwind CSS, a real
 * Chromium keyboard and a real touchscreen.
 *
 * Two deliberate decisions about what is real here:
 *
 *  1. **The catalogue lookup alone is answered in the browser.** The E2E fixture seeds no master
 *     products, and seeding them is a backend change that issue #1052 excludes, so
 *     `GET api/products/master-product-suggestions` (issue #1049) is fulfilled with two neutral
 *     synthetic size variants of one product. Everything else on the page - the actor, the
 *     categories, the GST rules - is the real API, and the contract of the stubbed response is the
 *     published `MasterProductSuggestionPage` shape the backend's own tests pin. What is under test
 *     is the rendered field, not the lookup.
 *  2. **It writes nothing.** It opens `/products/new`, types, measures boxes, and takes suggestions
 *     into the form's two fields. It never submits, so it creates no product, links no master
 *     product and needs no seeded row, and it is independent of every other file's ordering.
 */
const desktopViewport = { width: 1440, height: 900 };

/** The width issue #1052 names, which is the narrowest phone the application is checked at. */
const mobileViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

interface Suggestion {
  id: number;
  brand: string;
  name: string;
  sizeValue: number;
  sizeUnit: string;
  productName: string;
  unitLabel: string;
}

/**
 * Two size variants of one catalogue product, which is exactly the case the criterion names: the
 * brand and the name are identical and only the size tells them apart, so the list is only usable if
 * it renders the size.
 */
const smallVariant: Suggestion = {
  id: 9101,
  brand: 'Sample Brand',
  name: 'Tasty Cheese Corn Chips',
  sizeValue: 45,
  sizeUnit: 'g',
  productName: 'Sample Brand Tasty Cheese Corn Chips',
  unitLabel: '45g'
};

const largeVariant: Suggestion = {
  id: 9102,
  brand: 'Sample Brand',
  name: 'Tasty Cheese Corn Chips',
  sizeValue: 175,
  sizeUnit: 'g',
  productName: 'Sample Brand Tasty Cheese Corn Chips',
  unitLabel: '175g'
};

/** The term both variants match, and a term the synthetic catalogue holds nothing like. */
const matchingTerm = 'corn chips';
const unmatchedTerm = 'house blend loose leaf tea';

/** The label the list renders: the server's own `{Brand} {Name}`, then the size spaced for reading. */
function labelOf(suggestion: Suggestion): string {
  return `${suggestion.productName} · ${suggestion.sizeValue} ${suggestion.sizeUnit}`;
}

/**
 * Answers the catalogue lookup in the browser with the two variants when the term is part of their
 * name, and with an empty page otherwise. Nothing else is intercepted.
 */
async function stubCatalogueLookup(page: Page): Promise<void> {
  await page.route('**/api/products/master-product-suggestions**', async (route) => {
    const term = (new URL(route.request().url()).searchParams.get('q') ?? '').toLowerCase();
    const matches = 'sample brand tasty cheese corn chips'.includes(term) ? [smallVariant, largeVariant] : [];

    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ products: matches, page: 1, pageSize: 10, hasMore: false })
    });
  });
}

function nameField(page: Page): Locator {
  return page.locator('app-product-master-product-picker input[role="combobox"]');
}

function unitField(page: Page): Locator {
  return page.locator('input[name="unit"]');
}

function suggestionList(page: Page): Locator {
  return page.locator('app-product-master-product-picker [role="listbox"]');
}

function suggestionRows(page: Page): Locator {
  return page.locator('app-product-master-product-picker [role="option"]');
}

function selectionNote(page: Page): Locator {
  return page.locator('[data-testid="product-master-product-selection"]');
}

function trackingOptOut(page: Page): Locator {
  return page.locator('input[name="trackSupplierPrices"]');
}

async function expectNoHorizontalOverflow(page: Page, label: string): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  expect(overflow, `${label}: page overflow`).toBeLessThanOrEqual(tolerance);
}

/** The New Product page at one viewport, with the catalogue lookup answered in the browser. */
async function openNewProduct(page: Page, viewport: { width: number; height: number }): Promise<void> {
  await stubCatalogueLookup(page);
  await page.setViewportSize(viewport);
  await page.goto('/products/new');
  await expect(page.getByRole('heading', { name: 'New product', level: 1 })).toBeVisible();
}

/** Types a term into Name and waits for the list the debounced lookup answers with. */
async function searchFor(page: Page, term: string): Promise<void> {
  await nameField(page).fill(term);
  await expect(suggestionRows(page)).toHaveCount(2);
}

/** What a taken suggestion must have filled in, and the note it must have put on the page. */
async function expectPrefilledFrom(page: Page, suggestion: Suggestion, where: string): Promise<void> {
  await expect(nameField(page), `${where}: Name is the catalogue's own name`).toHaveValue(suggestion.productName);
  await expect(unitField(page), `${where}: Unit Label is the catalogue's own label`).toHaveValue(
    suggestion.unitLabel
  );
  await expect(selectionNote(page), `${where}: the tracking note is on the page`).toContainText(
    suggestion.unitLabel
  );
  await expect(trackingOptOut(page), `${where}: tracking is on by default`).toBeChecked();
  await expect(suggestionList(page), `${where}: the list closed behind the selection`).toHaveCount(0);
  await expect(nameField(page), `${where}: the combobox reports itself closed`).toHaveAttribute(
    'aria-expanded',
    'false'
  );
}

for (const viewport of [desktopViewport, mobileViewport]) {
  const at = `${viewport.width}x${viewport.height}`;

  test(`at ${at} the Name field lists both size variants, labelled apart, without overflowing`, async ({
    pageAs
  }) => {
    const page = await pageAs(actors.businessAOwner);
    await openNewProduct(page, viewport);

    // Nothing is offered for one character: the list only exists from the second (issue #1049's
    // minimum), which is also why no refused request is spent on the first keystroke.
    await nameField(page).fill('c');
    await expect(suggestionList(page), `${at}: nothing is listed for one character`).toHaveCount(0);

    await searchFor(page, matchingTerm);

    // The two rows are told apart by the size the list renders, which is the whole point of the
    // criterion: brand and name are identical on both.
    await expect(suggestionRows(page)).toHaveText([labelOf(smallVariant), labelOf(largeVariant)]);
    await expect(suggestionRows(page).first()).toHaveAttribute('aria-label', labelOf(smallVariant));
    await expect(suggestionRows(page).last()).toHaveAttribute('aria-label', labelOf(largeVariant));
    await expect(nameField(page), `${at}: the combobox reports itself open`).toHaveAttribute(
      'aria-expanded',
      'true'
    );

    // The open panel is laid out inside the viewport and pushes nothing sideways - the regression a
    // 390px check is for, and the one jsdom cannot see at all.
    const listBox = await suggestionList(page).boundingBox();
    expect(listBox, `${at}: the list has a laid-out box`).not.toBeNull();
    expect(listBox!.x, `${at}: inside the left viewport edge`).toBeGreaterThanOrEqual(-tolerance);
    expect(listBox!.x + listBox!.width, `${at}: inside the right viewport edge`).toBeLessThanOrEqual(
      viewport.width + tolerance
    );

    for (const row of await suggestionRows(page).all()) {
      const rowBox = await row.boundingBox();
      expect(rowBox, `${at}: the row has a laid-out box`).not.toBeNull();
      expect(rowBox!.x + rowBox!.width, `${at}: the row stays inside the panel`).toBeLessThanOrEqual(
        listBox!.x + listBox!.width + tolerance
      );
    }

    const listOverflow = await suggestionList(page).evaluate(
      (element) => element.scrollWidth - element.clientWidth
    );
    expect(listOverflow, `${at}: the list scrolls sideways`).toBeLessThanOrEqual(tolerance);
    await expectNoHorizontalOverflow(page, `products/new with the list open at ${at}`);
  });

  test(`at ${at} the keyboard takes one size variant and a pressed row takes the other`, async ({ pageAs }) => {
    const page = await pageAs(actors.businessAOwner);
    await openNewProduct(page, viewport);
    await searchFor(page, matchingTerm);

    // Arrow keys walk the rows and publish the active one, and Enter takes the row the keyboard is
    // on - the 175 g variant, not the first row and not the typed text.
    await nameField(page).press('ArrowDown');
    await expect(nameField(page)).toHaveAttribute(
      'aria-activedescendant',
      (await suggestionRows(page).first().getAttribute('id'))!
    );
    await nameField(page).press('ArrowDown');
    await expect(nameField(page)).toHaveAttribute(
      'aria-activedescendant',
      (await suggestionRows(page).last().getAttribute('id'))!
    );

    await nameField(page).press('Enter');
    await expectPrefilledFrom(page, largeVariant, `keyboard at ${at}`);

    // Taking the other variant starts a fresh selection, with the opt-out back on, and the two
    // fields now hold the 45 g variant's own values.
    await trackingOptOut(page).uncheck();
    await searchFor(page, matchingTerm);
    await suggestionRows(page).first().click();
    await expectPrefilledFrom(page, smallVariant, `pressed row at ${at}`);

    await expectNoHorizontalOverflow(page, `products/new after a selection at ${at}`);
  });

  test(`at ${at} the tracking opt-out and free text behave as the field promises`, async ({ pageAs }) => {
    const page = await pageAs(actors.businessAOwner);
    await openNewProduct(page, viewport);

    // Free text first, on an untouched form: a name the shared catalogue holds nothing like stays
    // exactly as it was typed, is said so in the panel, and fills nothing in anywhere.
    await nameField(page).fill(unmatchedTerm);
    await expect(
      page.locator('[data-testid="master-product-suggestions-empty"]'),
      `${at}: the panel says the catalogue holds nothing like it`
    ).toBeVisible();
    await expect(suggestionRows(page), `${at}: no row is offered`).toHaveCount(0);
    await expect(nameField(page), `${at}: the typed Name is untouched`).toHaveValue(unmatchedTerm);
    await expect(unitField(page), `${at}: free text fills nothing in`).toHaveValue('');
    await expect(selectionNote(page), `${at}: free text links nothing`).toHaveCount(0);
    await expectNoHorizontalOverflow(page, `products/new with the empty panel at ${at}`);

    await searchFor(page, matchingTerm);
    await suggestionRows(page).last().click();
    await expectPrefilledFrom(page, largeVariant, `opt-out at ${at}`);

    // The opt-out is the only thing the note offers, it unticks, and it takes neither the note nor
    // either filled-in value with it.
    await trackingOptOut(page).uncheck();
    await expect(trackingOptOut(page), `${at}: the opt-out is unticked`).not.toBeChecked();
    await expect(selectionNote(page), `${at}: the note stays`).toBeVisible();
    await expect(nameField(page)).toHaveValue(largeVariant.productName);
    await expect(unitField(page)).toHaveValue(largeVariant.unitLabel);

    // Editing the Name away from the selected product drops the selection, so the note and its
    // checkbox go: nothing is linked and nothing is tracked. The two values stay as they are - the
    // selection is what is dropped, not the operator's text - and the Unit Label is now the
    // operator's own to keep or change.
    await nameField(page).fill('Sample Brand Something Else Entirely');
    await expect(selectionNote(page), `${at}: the note goes with the selection`).toHaveCount(0);
    await expect(unitField(page), `${at}: the Unit Label is left as it stands`).toHaveValue(
      largeVariant.unitLabel
    );

    await expectNoHorizontalOverflow(page, `products/new after the selection is dropped at ${at}`);
  });
}

test('on a touchscreen at 390px a tapped row fills both fields in', async ({ pageAs }) => {
  // A real touchscreen rather than a mouse: "works by touch on mobile" is the one part of the
  // criterion a click cannot answer, because a row that only responds to a mouse would pass every
  // click-based check and still be unusable on a phone.
  const page = await pageAs(actors.businessAOwner, { hasTouch: true });
  await openNewProduct(page, mobileViewport);
  await searchFor(page, matchingTerm);

  await suggestionRows(page).last().tap();
  await expectPrefilledFrom(page, largeVariant, 'touch at 390x844');
  await expectNoHorizontalOverflow(page, 'products/new after a tapped row at 390x844');
});
