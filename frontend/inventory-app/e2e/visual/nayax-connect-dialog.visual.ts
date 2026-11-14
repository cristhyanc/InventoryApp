import { Page, Route, expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import {
  OWNER_CAPABILITIES,
  TOLERANCE as tolerance,
  expectNoPageHorizontalOverflow,
  expectPinchZoomAllowed
} from './support/suppliers-page';

/**
 * The browser-level evidence for issue #1311: the Connect Nayax dialog on Admin → Integrations at
 * 1440px and 390px - the three steps with Nayax's official guides linked, every distinct test state,
 * and the review of a clean, an empty-account, a partial-permission and an expired test.
 *
 * jsdom computes no layout and has no `<dialog>` top layer, so `nayax-connection-wizard.component.spec.ts`
 * proves the behaviour but not what this measures in Chromium: no sideways scroll of the page, the
 * panel or its body; a body that scrolls vertically at 390px while Back and the primary action stay
 * inside the viewport; no embedded Nayax image, with the official guides linked in a new tab; Escape
 * closing the dialog and focus going back to the launcher.
 *
 * It starts no API and no database. Every `/api/` request is answered in the browser with synthetic
 * data: the test and save calls are answered here and reach no server, so no Nayax account is
 * contacted and nothing is stored. The pasted token is a placeholder, typed into the field only, and
 * no Nayax image is on the page, so none can appear in a capture. It is human-invoked and is not part of `scripts/validate.sh`.
 */
const screenshotDirectory = resolve(__dirname, '..', '..', '..', '..', 'docs', 'screenshots', 'issue-1311');
mkdirSync(screenshotDirectory, { recursive: true });

const PLACEHOLDER_TOKEN = 'visual-run-placeholder-not-a-token';
const dialog = '[data-testid="connect-nayax-dialog"]';

type Scenario = 'passed' | 'zero-machines' | 'partial' | 'invalid-token' | 'timeout' | 'expired';

const passing = [
  { check: 'Machines', status: 'Passed', failure: null },
  { check: 'Machine', status: 'Passed', failure: null },
  { check: 'ProductCatalogue', status: 'Passed', failure: null },
  { check: 'ProductGroups', status: 'Passed', failure: null },
  { check: 'MachineProducts', status: 'Passed', failure: null },
  { check: 'MachineLastSales', status: 'Passed', failure: null },
  { check: 'MachineLastAlerts', status: 'Passed', failure: null }
];

function testAnswer(scenario: Scenario): unknown {
  const inFifteenMinutes = new Date(Date.now() + 15 * 60 * 1000).toISOString();
  const base = { draftId: '00000000-0000-4000-8000-000000001311', expiresAtUtc: inFifteenMinutes };
  switch (scenario) {
    case 'passed':
      return { ...base, machineCount: 3, nothingFailed: true, checks: passing };
    case 'zero-machines':
      return {
        ...base,
        machineCount: 0,
        nothingFailed: true,
        checks: passing.map((line) =>
          ['Machine', 'MachineProducts', 'MachineLastSales', 'MachineLastAlerts'].includes(line.check)
            ? { ...line, status: 'NotChecked' }
            : line
        )
      };
    case 'partial':
      return {
        ...base,
        machineCount: 3,
        nothingFailed: false,
        checks: passing.map((line) =>
          line.check === 'ProductGroups' || line.check === 'MachineLastSales'
            ? { ...line, status: 'Failed', failure: 'PermissionNotGranted' }
            : line
        )
      };
    case 'invalid-token':
      return {
        ...base,
        machineCount: null,
        nothingFailed: false,
        checks: [{ check: 'Machines', status: 'Failed', failure: 'InvalidOrExpiredToken' }]
      };
    case 'timeout':
      return {
        ...base,
        machineCount: null,
        nothingFailed: false,
        checks: [{ check: 'Machines', status: 'Failed', failure: 'NayaxUnavailable' }]
      };
    case 'expired':
      return { ...base, expiresAtUtc: new Date(Date.now() - 1000).toISOString(), machineCount: 3, nothingFailed: true, checks: passing };
  }
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function answerApi(page: Page, scenario: Scenario): Promise<void> {
  let stored = 'NotConfigured';
  await page.route('**/api/**', (route: Route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;

    if (path.endsWith('/nayax-connection/test') && request.method() === 'POST') {
      return json(route, testAnswer(scenario));
    }
    if (path.endsWith('/nayax-connection/save') && request.method() === 'POST') {
      stored = scenario === 'partial' ? 'PendingPermissions' : 'Ready';
      return json(route, { status: stored, machineChecksPending: scenario === 'zero-machines' });
    }
    if (request.method() !== 'GET') {
      return json(route, { title: 'This visual run makes no other writes.' }, 500);
    }
    if (path.endsWith('/me/access')) {
      return json(route, {
        role: 'Owner',
        capabilities: OWNER_CAPABILITIES,
        businessName: 'Sample Vending',
        timeZoneId: 'Australia/Sydney',
        entitlement: { enforced: true, plan: 'Pro', source: 'BillingSubscription', endsAtUtc: null }
      });
    }
    if (path.endsWith('/me/account-state')) return json(route, { state: 'Member', onboardingEnabled: false, rejectionReason: null });
    if (path.endsWith('/business/current')) return json(route, { name: 'Sample Vending', timeZoneId: 'Australia/Sydney' });
    if (path.endsWith('/nayax-connection/status')) return json(route, { status: stored, machineChecksPending: false });
    if (path.endsWith('/nayax-settings/processing-fee-rates') || path.includes('processing-fee')) {
      return json(route, [{ id: 1, effectiveFrom: '2026-07-01', feeExGst: 0.17 }]);
    }
    return json(route, []);
  });
}

async function openIntegrations(page: Page, width: number, height: number, scenario: Scenario): Promise<void> {
  await page.setViewportSize({ width, height });
  await answerApi(page, scenario);
  await page.goto('/admin/nayax-settings');
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }'
  });
  await expect(page.getByRole('heading', { name: 'Integrations', level: 1 })).toBeVisible();
  await expect(page.locator('[data-testid="open-connect"]')).toHaveText(/Connect Nayax/);
}

/** Nayax's screenshots are linked, never embedded: they show its demo account's identifiers (issue #1311). */
async function expectNoEmbeddedScreenshots(page: Page): Promise<void> {
  await expect(page.locator(`${dialog} img`)).toHaveCount(0);
  const links = page.locator(`${dialog} [data-testid="nayax-guide-source"] a`);
  expect(await links.count()).toBeGreaterThan(0);
  for (const href of await links.evaluateAll((elements) => elements.map((element) => element.getAttribute('href')))) {
    expect(href).toMatch(/^https:\/\/devzone\.nayax\.com\/docs\//);
  }
  for (const target of await links.evaluateAll((elements) => elements.map((element) => element.getAttribute('target')))) {
    expect(target).toBe('_blank');
  }
}

/** Nothing scrolls sideways - neither the page, nor the panel, nor its body - and the panel stays inside the viewport. */
async function expectNoHorizontalOverflow(page: Page, width: number): Promise<void> {
  await expectNoPageHorizontalOverflow(page, width);
  const overflow = await page.evaluate((selector) => {
    const panel = document.querySelector<HTMLElement>(selector);
    const body = panel?.querySelector<HTMLElement>('.app-dialog-body');
    return {
      panel: panel ? panel.scrollWidth - panel.clientWidth : 0,
      body: body ? body.scrollWidth - body.clientWidth : 0
    };
  }, dialog);
  expect(overflow.panel, `the dialog does not scroll sideways at ${width}px`).toBeLessThanOrEqual(tolerance);
  expect(overflow.body, `the dialog body does not scroll sideways at ${width}px`).toBeLessThanOrEqual(tolerance);
  const box = (await page.locator(dialog).boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(-tolerance);
  expect(box.x + box.width).toBeLessThanOrEqual(width + tolerance);
}

/** Back and the current primary action are inside the viewport without scrolling the body. */
async function expectFooterReachable(page: Page, width: number, height: number): Promise<void> {
  const footer = (await page.locator(`${dialog} .app-dialog-footer`).boundingBox())!;
  expect(footer.y + footer.height, `the action row is inside the viewport at ${width}px`).toBeLessThanOrEqual(height + tolerance);
  const primary = page.locator(`${dialog} .app-dialog-footer .btn-primary`);
  await expect(primary).toBeInViewport();
}

async function capture(page: Page, width: number, height: number, name: string): Promise<void> {
  await expectNoHorizontalOverflow(page, width);
  await expectFooterReachable(page, width, height);
  await page.screenshot({ path: join(screenshotDirectory, `${name}-${width}px.png`) });
}

/** A phone capture of the whole scrollable body, so everything below the fold is evidence too. */
async function captureBody(page: Page, width: number, name: string): Promise<void> {
  await page.locator(`${dialog} .app-dialog-body`).screenshot({ path: join(screenshotDirectory, `${name}-body-${width}px.png`) });
}

async function toTokenStep(page: Page): Promise<void> {
  await page.locator('[data-testid="open-connect"]').click();
  await expect(page.locator(dialog)).toBeVisible();
  await expect(page.locator('#nayax-operator-id')).toBeFocused();
  await page.locator('#nayax-operator-id').fill('1234567');
  await page.locator('[data-testid="next"]').click();
  await expect(page.locator('#nayax-token')).toHaveValue('');
}

async function runTest(page: Page): Promise<void> {
  await page.locator('#nayax-token').fill(PLACEHOLDER_TOKEN);
  await page.locator('[data-testid="run-test"]').click();
}

const widths = [
  { width: 1440, height: 900 },
  { width: 390, height: 844 }
];

test.describe('The Connect Nayax dialog (issue #1311)', () => {
  test.beforeEach(() => {
    test.skip(test.info().project.name !== 'desktop-1440', 'Each width is set inside the test.');
  });

  for (const { width, height } of widths) {
    test(`the three steps of a clean connection at ${width}px`, async ({ page }) => {
      await openIntegrations(page, width, height, 'passed');
      await expectPinchZoomAllowed(page);

      await page.locator('[data-testid="open-connect"]').click();
      await expect(page.locator(dialog)).toBeVisible();
      await expect(page.locator(`${dialog} [data-testid="step-1"]`)).toContainText('In Nayax Core');
      await expect(page.locator(`${dialog} [data-testid="step-1"]`)).toContainText('In Restockroo');
      await expect(page.locator(`${dialog} [data-testid="open-nayax-core"]`)).toHaveAttribute('target', '_blank');
      await expectNoEmbeddedScreenshots(page);
      await capture(page, width, height, 'step-1-operator-id');
      if (width === 390) {
        await captureBody(page, width, 'step-1-operator-id');
      }

      await page.locator('#nayax-operator-id').fill('1234567');
      await page.locator('[data-testid="next"]').click();
      await expect(page.locator('#nayax-token')).toHaveValue('');
      await expect(page.locator('#nayax-token')).toHaveAttribute('type', 'password');
      await expectNoEmbeddedScreenshots(page);

      if (width === 390) {
        // The guide is stacked above the field, not beside it.
        const guide = (await page.locator(`${dialog} [data-testid="token-instructions"]`).boundingBox())!;
        const field = (await page.locator('#nayax-token').boundingBox())!;
        expect(field.y).toBeGreaterThan(guide.y + guide.height - tolerance);
      } else {
        const guide = (await page.locator(`${dialog} [data-testid="token-instructions"]`).boundingBox())!;
        const field = (await page.locator('#nayax-token').boundingBox())!;
        expect(field.x, 'the field sits beside the guide at 1440px').toBeGreaterThan(guide.x + guide.width - tolerance);
      }
      await capture(page, width, height, 'step-2-lynx-token');
      if (width === 390) {
        await captureBody(page, width, 'step-2-lynx-token');
      }

      await runTest(page);
      await expect(page.locator(`${dialog} [data-testid="review-passed"]`)).toContainText('Connection test passed.');
      await expect(page.locator(`${dialog}`)).not.toContainText('Nayax connected');
      await expect(page.locator(`${dialog} [data-testid="check-Machines"]`)).toHaveCount(1);
      await expect(page.locator(dialog)).not.toContainText('Signing in');
      await capture(page, width, height, 'step-3-review-passed');

      await page.locator('[data-testid="save-connection"]').click();
      await expect(page.locator(`${dialog} [data-testid="saved"]`)).toContainText('Nayax connected');
      await capture(page, width, height, 'step-3-saved');

      await page.locator('[data-testid="done"]').click();
      await expect(page.locator(dialog)).toHaveCount(0);
      await expect(page.locator('[data-testid="open-connect"]')).toHaveText(/Replace connection/);
      await expect(page.locator('[data-testid="open-connect"]')).toBeFocused();
      await page.screenshot({ path: join(screenshotDirectory, `integrations-connected-${width}px.png`), fullPage: true });
    });

    test(`a partial-permission review at ${width}px`, async ({ page }) => {
      await openIntegrations(page, width, height, 'partial');
      await toTokenStep(page);
      await runTest(page);

      await expect(page.locator(`${dialog} [data-testid="review-partial"]`)).toBeVisible();
      await expect(page.locator(`${dialog} [data-testid="save-connection"]`)).toBeEnabled();
      const request = await page.locator('[data-testid="support-request"]').inputValue();
      expect(request).toContain('Operator ID: 1234567');
      expect(request).toContain('Operator product groups');
      expect(request).toContain('Machine recent sales');
      expect(request).not.toContain('Machine list');
      expect(request).not.toContain(PLACEHOLDER_TOKEN);
      if (width === 390) {
        // The body is the one thing that scrolls on a phone, vertically, with the action row fixed.
        const body = page.locator(`${dialog} .app-dialog-body`);
        await expect(body).toHaveCSS('overflow-y', 'auto');
        const scrolls = await body.evaluate((element) => element.scrollHeight > element.clientHeight);
        expect(scrolls, 'the partial review scrolls vertically at 390px').toBe(true);
      }
      await capture(page, width, height, 'step-3-review-partial');
      await captureBody(page, width, 'step-3-review-partial');
    });

    test(`the zero-machines review at ${width}px`, async ({ page }) => {
      await openIntegrations(page, width, height, 'zero-machines');
      await toTokenStep(page);
      await runTest(page);

      await expect(page.locator(`${dialog} [data-testid="review-zero-machines"]`)).toContainText(
        'Connection test passed. No machines found yet.'
      );
      await expect(page.locator(`${dialog} [data-testid="save-connection"]`)).toBeEnabled();
      await capture(page, width, height, 'step-3-review-zero-machines');
    });

    test(`the expired-test review at ${width}px`, async ({ page }) => {
      await openIntegrations(page, width, height, 'expired');
      await toTokenStep(page);
      await runTest(page);

      await expect(page.locator(`${dialog} [data-testid="review-expired"]`)).toBeVisible();
      await expect(page.locator(`${dialog} [data-testid="save-connection"]`)).toHaveCount(0);
      await capture(page, width, height, 'step-3-review-expired');
    });

    for (const scenario of ['invalid-token', 'timeout'] as const) {
      test(`the ${scenario} state at ${width}px`, async ({ page }) => {
        await openIntegrations(page, width, height, scenario);
        await toTokenStep(page);
        await runTest(page);

        await expect(page.locator(`${dialog} [data-testid="state-${scenario}"]`)).toBeVisible();
        await expect(page.locator('#nayax-token')).toHaveValue('');
        await expect(page.locator('#nayax-token')).toBeFocused();
        if (width === 390) {
          await page.locator(`${dialog} [data-testid="state-${scenario}"]`).scrollIntoViewIfNeeded();
        }
        await capture(page, width, height, `step-2-${scenario}`);
      });
    }

    test(`Escape closes the dialog, clears the token and returns focus at ${width}px`, async ({ page }) => {
      await openIntegrations(page, width, height, 'passed');
      await toTokenStep(page);
      await page.locator('#nayax-token').fill(PLACEHOLDER_TOKEN);

      // Tab stays inside the dialog however far it goes.
      for (let index = 0; index < 25; index++) {
        await page.keyboard.press('Tab');
        const inside = await page.evaluate((selector) => document.querySelector(selector)?.contains(document.activeElement) ?? false, dialog);
        expect(inside, 'focus stays inside the dialog').toBe(true);
      }

      await page.keyboard.press('Escape');
      await expect(page.locator(dialog)).toHaveCount(0);
      await expect(page.locator('#nayax-token')).toHaveCount(0);
      await expect(page.locator('[data-testid="open-connect"]')).toBeFocused();

      await page.locator('[data-testid="open-connect"]').click();
      await expect(page.locator('#nayax-operator-id')).toHaveValue('');
    });
  }
});
