import { Locator, Page, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * The visual evidence issue #409's series rules require for #456: the sidebar's outlined
 * navigation glyphs and its relocated collapse control, captured at 1440px and 390px from the real
 * shell in a real browser.
 *
 * Everything on screen is neutral: the run starts the Angular dev server alone, with no API and no
 * database, so the only two pages it opens are the development-only design-system fixture (which
 * calls nothing) and `/machines`, which is opened purely so one navigation item is the active one.
 * That page's own data request cannot be answered and is not what is captured — every screenshot
 * is of the navigation panel or the top bar, never of business data, an account name or a
 * credential.
 */
const FIXTURE_PATH = '/__design-system/widgets';

/** A real navigation destination, so a screenshot shows the active item beside inactive ones. */
const ACTIVE_ITEM_PATH = '/machines';

/** The `lg` breakpoint the shell itself reads: below it the sidebar is a drawer, not a rail. */
const WIDE_LAYOUT_WIDTH = 1024;

const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-456');
mkdirSync(screenshotDirectory, { recursive: true });

/** `desktop-1440` / `mobile-390` -> `1440px` / `390px`, so a file name states its own width. */
function widthSuffix(projectName: string): string {
  const width = projectName.split('-').pop();
  return `${width}px`;
}

function isWideLayout(page: Page): boolean {
  return (page.viewportSize()?.width ?? 0) >= WIDE_LAYOUT_WIDTH;
}

function navigation(page: Page): Locator {
  return page.locator('nav[aria-label="Primary"]');
}

async function open(page: Page, path: string): Promise<void> {
  await page.goto(path);

  // Toast slide-in and the spinner rotation would otherwise decide what a screenshot caught.
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });

  if (!isWideLayout(page)) {
    // A narrow layout starts with the drawer dismissed, so there is nothing to photograph until
    // the top bar's opener - the one control that stayed there - is used.
    await page.getByRole('button', { name: 'Open navigation menu' }).click();
  }

  await expect(navigation(page)).toBeVisible();
}

async function capture(locator: Locator, name: string): Promise<void> {
  await expect(locator).toBeVisible();
  await locator.screenshot({ path: join(screenshotDirectory, `${name}-${widthSuffix(test.info().project.name)}.png`) });
}

test.describe('sidebar navigation visual evidence', () => {
  test('navigation with labels', async ({ page }) => {
    await open(page, FIXTURE_PATH);

    await capture(navigation(page), isWideLayout(page) ? 'sidebar-expanded' : 'sidebar-drawer');
  });

  test('navigation with the active item', async ({ page }) => {
    await open(page, ACTIVE_ITEM_PATH);
    await expect(navigation(page).locator('a[aria-current="page"]')).toBeVisible();

    await capture(navigation(page), 'sidebar-active-item');
  });

  test('navigation with its groups open', async ({ page }) => {
    await open(page, FIXTURE_PATH);
    await Promise.all(
      ['Products', 'Purchases', 'Reports'].map((group) => navigation(page).getByRole('button', { name: group }).click())
    );

    await capture(navigation(page), 'sidebar-groups-open');
  });

  test('collapsed navigation rail', async ({ page }) => {
    test.skip(!isWideLayout(page), 'A narrow layout has a drawer, not a collapsible rail.');
    await open(page, ACTIVE_ITEM_PATH);

    await navigation(page).getByRole('button', { name: 'Collapse navigation' }).click();
    await expect(navigation(page).getByRole('button', { name: 'Expand navigation' })).toBeVisible();

    await capture(navigation(page), 'sidebar-collapsed');
  });

  test('top bar beside the navigation', async ({ page }) => {
    // At 1440px this is the evidence that no detached toggle is left floating above the menu; at
    // 390px it is the evidence that the drawer's opener is still there.
    await page.goto(FIXTURE_PATH);
    await page.addStyleTag({
      content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
    });

    // `getByRole('banner')` rather than `header`: the fixture page has a nested `<header>` of its
    // own inside `<main>`, which is not a banner landmark and is not what this captures.
    await capture(page.getByRole('banner'), 'shell-top-bar');
  });
});
