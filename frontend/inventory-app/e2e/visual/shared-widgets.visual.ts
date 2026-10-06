import { Locator, Page, expect, test } from '@playwright/test';
import { join, resolve } from 'node:path';

/**
 * The visual evidence issue #409's series rules require for #411: the four restyled shared
 * widgets and the whole bundled icon set, captured at 1440px and 390px from the real components
 * running in the real application shell.
 *
 * Everything on screen is neutral synthetic sample content from the development-only fixture
 * route (`src/app/design-system/widget-gallery.component.ts`). The run starts no API, reads no
 * database and reaches no external service, so no business data, account name or credential can
 * appear in a screenshot.
 */
const FIXTURE_PATH = '/__design-system/widgets';
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-411');

/** `desktop-1440` / `mobile-390` -> `1440px` / `390px`, so a file name states its own width. */
function widthSuffix(projectName: string): string {
  const width = projectName.split('-').pop();
  return `${width}px`;
}

async function openFixture(page: Page): Promise<void> {
  await page.goto(FIXTURE_PATH);
  await expect(page.getByTestId('widget-gallery')).toBeVisible();

  // Toast slide-in and the spinner rotation would otherwise decide what a screenshot caught.
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; }'
  });
}

async function capture(locator: Locator, name: string): Promise<void> {
  await expect(locator).toBeVisible();
  await locator.screenshot({ path: join(screenshotDirectory, `${name}-${widthSuffix(test.info().project.name)}.png`) });
}

test.describe('shared widget and icon visual evidence', () => {
  test('bundled icon set', async ({ page }) => {
    await openFixture(page);

    await capture(page.getByTestId('icon-strip'), 'icons');
  });

  test('confirmation dialog', async ({ page }) => {
    await openFixture(page);
    await page.getByTestId('open-confirmation').click();

    await capture(page.locator('.confirm-modal'), 'confirmation-dialog');
  });

  test('loading indicator', async ({ page }) => {
    await openFixture(page);
    await page.getByTestId('show-loading').click();

    await capture(page.locator('[role="status"]'), 'loading-indicator');
  });

  for (const variant of ['success', 'warning', 'error', 'info'] as const) {
    test(`${variant} toast`, async ({ page }) => {
      await openFixture(page);
      await page.getByTestId(`show-toast-${variant}`).click();

      await capture(page.locator('.toast'), `toast-${variant}`);
    });
  }

  test('open multi-select dropdown', async ({ page }) => {
    await openFixture(page);
    const section = page.getByTestId('dropdown-section');
    await section.getByRole('button').click();

    await expect(section.getByRole('group')).toBeVisible();
    await capture(section, 'multi-select-dropdown-open');
  });
});
