import { actors, expect, test } from '../fixtures/harness';

/**
 * The rendered geometry of the all-products checkbox on Inventory AVCO Transition Baseline (issue
 * #872): the global `input` rule in `styles.scss` stretches every checkbox to the full input
 * width, which drew its tick near the middle of a roughly 580px-wide box and pushed the label text
 * well to the right. `h-4 w-4 shrink-0` on this one checkbox keeps it at its native size next to
 * the label text it has always sat inside.
 *
 * This is a browser test because jsdom computes no layout: the Jest spec beside the component
 * asserts the checkbox stays the label's one child element in document order, but only a real,
 * compiled-Tailwind render can answer "is the box small" and "does the label text start right next
 * to it" in pixels. It is also the one place that checks the checkbox still behaves as a native
 * control once it is sized: clicking the label text and pressing Space on the focused checkbox
 * both still toggle it, and it never previews or applies a baseline and writes nothing - every
 * check restores the checkbox to unchecked before moving on.
 *
 * `businessAOwner` is used because this page is provider-locked (issue #869): with no usable
 * Nayax connection it shows the connection-required page instead. Neither seeded E2E business
 * stores Nayax credentials, so the connection status read should answer `NotConfigured` - except
 * that, on this host, `GET /api/settings/nayax-connection/status` itself 500s, because the
 * dedicated E2E environment registers no `INayaxConnectionCheckRunner` (it skips
 * `AddNayaxLynxClient` entirely, the one place that service is registered, the same way it swaps in
 * `E2ETestNayaxLynxClient`). A failed status read is `unknown`, and `unknown` fails open
 * (`providerFeatureOffered`), which is what lets this page open for a seeded business with no real
 * connection at all. If that 500 is ever fixed without also seeding a usable connection, this file
 * starts failing with the connection-required page in its place, which is the correct signal to
 * open a follow-up issue for seeding one rather than editing this file's expectations.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

const labelText = 'Create baselines for all products that do not already have one';

/** The label's own text node, as its own laid-out box - not the label's, which includes the checkbox. */
async function labelTextBox(label: ReturnType<import('@playwright/test').Page['locator']>) {
  return label.evaluate((element) => {
    const textNode = Array.from(element.childNodes).find(
      (node) => node.nodeType === Node.TEXT_NODE && (node.textContent ?? '').trim().length > 0
    );
    if (!textNode) {
      throw new Error('expected the label to have a text node beside the checkbox');
    }
    const range = document.createRange();
    range.selectNodeContents(textNode);
    const rect = range.getBoundingClientRect();
    return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
  });
}

test('on a desktop screen the checkbox is small and sits immediately before the label text', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/admin/avco-transition');

  const label = page.locator('label').filter({ hasText: labelText });
  const checkbox = label.getByRole('checkbox');
  await expect(checkbox, 'the connection-required page has no checkbox; see the file doc comment').toBeVisible();
  await expect(checkbox).not.toBeChecked();

  const checkboxBox = (await checkbox.boundingBox())!;
  expect(checkboxBox.width).toBeLessThanOrEqual(24);
  expect(checkboxBox.height).toBeLessThanOrEqual(24);

  const textBox = await labelTextBox(label);
  // Same line: the two boxes' vertical spans overlap.
  expect(checkboxBox.y).toBeLessThan(textBox.y + textBox.height);
  expect(textBox.y).toBeLessThan(checkboxBox.y + checkboxBox.height);
  // Immediately before, with a small consistent gap: the text starts after the checkbox's right
  // edge, within the 16px the issue allows for the gap.
  const gap = textBox.x - (checkboxBox.x + checkboxBox.width);
  expect(gap).toBeGreaterThanOrEqual(0);
  expect(gap).toBeLessThanOrEqual(16);

  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(tolerance);
});

test('on a narrow screen the checkbox does not shrink or overlap, and nothing overflows', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(narrowViewport);
  await page.goto('/admin/avco-transition');

  const label = page.locator('label').filter({ hasText: labelText });
  const checkbox = label.getByRole('checkbox');
  await expect(checkbox).toBeVisible();
  await expect(checkbox).not.toBeChecked();

  const checkboxBox = (await checkbox.boundingBox())!;
  // Still its natural size, not squeezed by the narrow viewport.
  expect(checkboxBox.width).toBeGreaterThanOrEqual(14);
  expect(checkboxBox.width).toBeLessThanOrEqual(24);

  const textBox = await labelTextBox(label);
  // No overlap: the text starts at or after the checkbox's own right edge.
  expect(textBox.x).toBeGreaterThanOrEqual(checkboxBox.x + checkboxBox.width - tolerance);

  const pageOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(pageOverflow).toBeLessThanOrEqual(tolerance);
});

test('clicking the label text and pressing Space on the focused checkbox each toggle it', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await page.setViewportSize(desktopViewport);
  await page.goto('/admin/avco-transition');

  const label = page.locator('label').filter({ hasText: labelText });
  const checkbox = label.getByRole('checkbox');
  await expect(checkbox).not.toBeChecked();

  // The checkbox is still inside the label, so clicking the label's own text toggles it.
  await label.getByText(labelText).click();
  await expect(checkbox).toBeChecked();
  await label.getByText(labelText).click();
  await expect(checkbox).not.toBeChecked();

  // Space toggles the focused checkbox, as any native checkbox does.
  await checkbox.focus();
  await page.keyboard.press('Space');
  await expect(checkbox).toBeChecked();
  await page.keyboard.press('Space');
  await expect(checkbox).not.toBeChecked();
});
